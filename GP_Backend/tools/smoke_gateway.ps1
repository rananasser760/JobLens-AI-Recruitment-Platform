param(
    [string]$BaseUrl = "http://127.0.0.1:5088"
)

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Uri,
        $Body = $null,
        $Headers = $null
    )

    $invokeParams = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
    }

    if ($PSVersionTable.PSEdition -eq "Desktop") {
        $invokeParams["UseBasicParsing"] = $true
    }

    try {
        if ($null -ne $Body) {
            $jsonBody = $Body | ConvertTo-Json -Depth 20
            $invokeParams["ContentType"] = "application/json"
            $invokeParams["Body"] = $jsonBody
        }

        $resp = Invoke-WebRequest @invokeParams
        $parsed = $null
        if (-not [string]::IsNullOrWhiteSpace($resp.Content)) {
            try {
                $parsed = $resp.Content | ConvertFrom-Json
            }
            catch {
                $parsed = $null
            }
        }

        return [PSCustomObject]@{
            Ok = $true
            Status = [int]$resp.StatusCode
            Response = $parsed
            Raw = $resp.Content
        }
    }
    catch {
        $status = 0
        $raw = $_.Exception.Message

        if ($_.Exception.Response) {
            try {
                $status = [int]$_.Exception.Response.StatusCode
            }
            catch { }

            try {
                $stream = $_.Exception.Response.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $raw = $reader.ReadToEnd()
                }
            }
            catch { }
        }

        return [PSCustomObject]@{
            Ok = $false
            Status = $status
            Response = $null
            Raw = $raw
        }
    }
}

function Register-User {
    param(
        [string]$Role,
        [string]$Suffix,
        [string]$Password
    )

    $safeRole = $Role.ToLowerInvariant()
    $email = "smoke.$safeRole.$Suffix@example.com"
    $username = "smoke${safeRole}$Suffix"

    $registerBody = @{
        username = $username
        email = $email
        password = $Password
        confirmPassword = $Password
        role = $Role
        fullName = "Smoke $Role"
    }

    $registerResult = Invoke-Api -Method "POST" -Uri "$BaseUrl/api/auth/register" -Body $registerBody
    if (-not $registerResult.Ok -or $registerResult.Status -ne 200) {
        Write-Output "REGISTER_FAILED|role=$Role|status=$($registerResult.Status)|raw=$($registerResult.Raw)"
        return $null
    }

    $token = $registerResult.Response.data.accessToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        Write-Output "TOKEN_MISSING|role=$Role|raw=$($registerResult.Raw)"
        return $null
    }

    return [PSCustomObject]@{
        Role = $Role
        Token = $token
    }
}

function Assert-Status {
    param(
        [string]$Name,
        [string]$Role,
        $Result,
        [int[]]$ExpectedStatuses
    )

    $isExpected = $ExpectedStatuses -contains $Result.Status
    if ($isExpected) {
        $message = $null
        if ($Result.Response -and $Result.Response.message) {
            $message = $Result.Response.message
        }
        Write-Output ("PASS|role={0}|test={1}|status={2}|message={3}" -f $Role, $Name, $Result.Status, $message)
    }
    else {
        Write-Output ("FAIL|role={0}|test={1}|status={2}|expected={3}|raw={4}" -f $Role, $Name, $Result.Status, ($ExpectedStatuses -join ","), $Result.Raw)
    }
}

$resumeText = "John Doe`nSenior .NET Developer`nSkills: C#, ASP.NET Core, SQL Server, Python, FastAPI, Docker"
$jobDesc = "Looking for backend engineer with C#, ASP.NET Core, SQL and API integration experience"

function Run-CandidateSuite {
    param($Headers)

    $tests = @(
        @{ Name = "parse-text"; Method = "POST"; Uri = "$BaseUrl/api/resumes/parse-text"; Body = @{ resumeText = $resumeText }; Expected = @(200) },
        @{ Name = "ats-score-text"; Method = "POST"; Uri = "$BaseUrl/api/resumes/ats-score-text"; Body = @{ resumeText = $resumeText; jobDescription = $jobDesc }; Expected = @(200) },
        @{ Name = "improvements"; Method = "POST"; Uri = "$BaseUrl/api/resumes/improvements"; Body = @{ resumeText = $resumeText }; Expected = @(200) },
        @{ Name = "full-analysis"; Method = "POST"; Uri = "$BaseUrl/api/resumes/full-analysis"; Body = @{ resumeText = $resumeText; includeImprovements = $true; jobMatchLimit = 3 }; Expected = @(200) },
        @{ Name = "match-from-text"; Method = "POST"; Uri = "$BaseUrl/api/jobs/recommendations/match-from-text?limit=3"; Body = @{ resumeText = $resumeText }; Expected = @(200) },
        @{ Name = "scraping-status-forbidden"; Method = "GET"; Uri = "$BaseUrl/api/jobs/scraping/status"; Body = $null; Expected = @(403) },
        @{ Name = "scraping-jobs-forbidden"; Method = "GET"; Uri = "$BaseUrl/api/jobs/scraping/jobs?limit=5"; Body = $null; Expected = @(403) },
        @{ Name = "recruitment-status-forbidden"; Method = "GET"; Uri = "$BaseUrl/api/jobs/recruitment/status"; Body = $null; Expected = @(403) },
        @{ Name = "scraping-trigger-forbidden"; Method = "POST"; Uri = "$BaseUrl/api/jobs/scraping/trigger?maxCategories=1"; Body = $null; Expected = @(403) }
    )

    foreach ($test in $tests) {
        $result = Invoke-Api -Method $test.Method -Uri $test.Uri -Body $test.Body -Headers $Headers
        Assert-Status -Name $test.Name -Role "Candidate" -Result $result -ExpectedStatuses $test.Expected
    }
}

function Run-RecruiterSuite {
    param($Headers)

    $tests = @(
        @{ Name = "parse-text"; Method = "POST"; Uri = "$BaseUrl/api/resumes/parse-text"; Body = @{ resumeText = $resumeText }; Expected = @(200) },
        @{ Name = "scraping-status"; Method = "GET"; Uri = "$BaseUrl/api/jobs/scraping/status"; Body = $null; Expected = @(200) },
        @{ Name = "scraping-jobs"; Method = "GET"; Uri = "$BaseUrl/api/jobs/scraping/jobs?limit=5"; Body = $null; Expected = @(200) },
        @{ Name = "recruitment-status"; Method = "GET"; Uri = "$BaseUrl/api/jobs/recruitment/status"; Body = $null; Expected = @(200) },
        @{ Name = "candidate-recommendations-forbidden"; Method = "GET"; Uri = "$BaseUrl/api/jobs/recommendations?limit=3"; Body = $null; Expected = @(403) },
        @{ Name = "scraping-trigger-forbidden"; Method = "POST"; Uri = "$BaseUrl/api/jobs/scraping/trigger?maxCategories=1"; Body = $null; Expected = @(403) }
    )

    foreach ($test in $tests) {
        $result = Invoke-Api -Method $test.Method -Uri $test.Uri -Body $test.Body -Headers $Headers
        Assert-Status -Name $test.Name -Role "Recruiter" -Result $result -ExpectedStatuses $test.Expected
    }
}

function Run-AdminSuite {
    param($Headers)

    $tests = @(
        @{ Name = "parse-text"; Method = "POST"; Uri = "$BaseUrl/api/resumes/parse-text"; Body = @{ resumeText = $resumeText }; Expected = @(200) },
        @{ Name = "scraping-status"; Method = "GET"; Uri = "$BaseUrl/api/jobs/scraping/status"; Body = $null; Expected = @(200) },
        @{ Name = "scraping-jobs"; Method = "GET"; Uri = "$BaseUrl/api/jobs/scraping/jobs?limit=5"; Body = $null; Expected = @(200) },
        @{ Name = "recruitment-status"; Method = "GET"; Uri = "$BaseUrl/api/jobs/recruitment/status"; Body = $null; Expected = @(200) },
        @{ Name = "candidate-recommendations-forbidden"; Method = "GET"; Uri = "$BaseUrl/api/jobs/recommendations?limit=3"; Body = $null; Expected = @(403) },
        @{ Name = "scraping-trigger"; Method = "POST"; Uri = "$BaseUrl/api/jobs/scraping/trigger?maxCategories=1"; Body = $null; Expected = @(202, 502) }
    )

    foreach ($test in $tests) {
        $result = Invoke-Api -Method $test.Method -Uri $test.Uri -Body $test.Body -Headers $Headers
        Assert-Status -Name $test.Name -Role "Admin" -Result $result -ExpectedStatuses $test.Expected
    }
}

$ts = Get-Date -Format "yyyyMMddHHmmss"
$password = "P@ssw0rd!123"

$candidate = Register-User -Role "Candidate" -Suffix $ts -Password $password
$recruiter = Register-User -Role "Recruiter" -Suffix $ts -Password $password
$admin = Register-User -Role "Admin" -Suffix $ts -Password $password

if ($null -eq $candidate -or $null -eq $recruiter -or $null -eq $admin) {
    Write-Output "SETUP_FAILED|One or more role registrations failed"
    exit 1
}

$candidateHeaders = @{ Authorization = "Bearer $($candidate.Token)" }
$recruiterHeaders = @{ Authorization = "Bearer $($recruiter.Token)" }
$adminHeaders = @{ Authorization = "Bearer $($admin.Token)" }

Run-CandidateSuite -Headers $candidateHeaders
Run-RecruiterSuite -Headers $recruiterHeaders
Run-AdminSuite -Headers $adminHeaders
