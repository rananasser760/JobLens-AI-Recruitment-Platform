"""
Example Test Script for JobLens API
Demonstrates how to use the API endpoints
"""

import requests
import json
import time
from typing import List, Dict

# API Base URL
BASE_URL = "http://127.0.0.1:8000"


def print_section(title: str):
    """Print a formatted section header"""
    print("\n" + "="*60)
    print(f"  {title}")
    print("="*60 + "\n")


def test_health_check():
    """Test API health check"""
    print_section("1. Health Check")
    
    response = requests.get(f"{BASE_URL}/")
    print(f"Status: {response.status_code}")
    print(f"Response: {json.dumps(response.json(), indent=2)}")


def test_get_categories():
    """Test getting available categories"""
    print_section("2. Get Available Categories")
    
    response = requests.get(f"{BASE_URL}/api/categories")
    data = response.json()
    
    print(f"Total Categories: {len(data['data']['all_categories'])}")
    print(f"\nHigh-Demand Categories:")
    for cat in data['data']['high_demand_categories']:
        print(f"  - {cat}")
    
    print(f"\nAll Categories:")
    for cat in data['data']['all_categories']:
        print(f"  - {cat}")


def test_trigger_scraping():
    """Test triggering a scraping job"""
    print_section("3. Trigger Scraping Job")
    
    payload = {
        "sources": ["wuzzuf", "linkedin"],
        "categories": [
            "IT & Software Development",
            "Data & AI"
        ],
        "max_pages": 2,
        "get_details": True
    }
    
    print(f"Request Payload:")
    print(json.dumps(payload, indent=2))
    
    response = requests.post(
        f"{BASE_URL}/api/scraping/trigger",
        json=payload
    )
    
    print(f"\nStatus: {response.status_code}")
    result = response.json()
    print(f"Response: {json.dumps(result, indent=2)}")
    
    return result.get('data', {}).get('job_id')


def test_scraping_status(job_id: str):
    """Test checking scraping status"""
    print_section("4. Check Scraping Status")
    
    response = requests.get(f"{BASE_URL}/api/scraping/status/{job_id}")
    
    print(f"Status: {response.status_code}")
    print(f"Response: {json.dumps(response.json(), indent=2)}")


def test_get_jobs_basic():
    """Test getting jobs with basic filters"""
    print_section("5. Get Jobs - Basic Search")
    
    # Test 1: Get all jobs
    print("Test 1: Get first 10 jobs")
    response = requests.get(
        f"{BASE_URL}/api/scraping/jobs",
        params={"limit": 10}
    )
    
    data = response.json()
    print(f"Status: {response.status_code}")
    print(f"Total Found: {data.get('total_count', 0)}")
    
    if data.get('data'):
        print(f"\nSample Jobs:")
        for i, job in enumerate(data['data'][:3], 1):
            print(f"\n  {i}. {job['title']}")
            print(f"     Company: {job['company_name']}")
            print(f"     Location: {job['location']}")
            print(f"     Source: {job['external_source']}")
            print(f"     Category: {job.get('category', 'N/A')}")


def test_get_jobs_by_keyword():
    """Test semantic search by keyword"""
    print_section("6. Get Jobs - Keyword Search")
    
    keywords = ["python developer", "data scientist", "frontend engineer"]
    
    for keyword in keywords:
        print(f"\nSearching for: '{keyword}'")
        response = requests.get(
            f"{BASE_URL}/api/scraping/jobs",
            params={
                "keyword": keyword,
                "limit": 5
            }
        )
        
        data = response.json()
        print(f"  Found: {data.get('total_count', 0)} jobs")
        
        if data.get('data'):
            for job in data['data'][:2]:
                print(f"    - {job['title']} at {job['company_name']}")


def test_get_jobs_by_category():
    """Test filtering by category"""
    print_section("7. Get Jobs - Category Filter")
    
    categories = [
        "IT & Software Development",
        "Data & AI",
        "Engineering - Civ/Arch"
    ]
    
    for category in categories:
        print(f"\nCategory: {category}")
        response = requests.get(
            f"{BASE_URL}/api/scraping/jobs",
            params={
                "category": category,
                "limit": 5
            }
        )
        
        data = response.json()
        print(f"  Found: {data.get('total_count', 0)} jobs")
        
        if data.get('data'):
            for job in data['data'][:3]:
                print(f"    - {job['title']}")


def test_get_jobs_by_location():
    """Test filtering by location"""
    print_section("8. Get Jobs - Location Filter")
    
    locations = ["Cairo", "Giza", "Alexandria", "Remote"]
    
    for location in locations:
        print(f"\nLocation: {location}")
        response = requests.get(
            f"{BASE_URL}/api/scraping/jobs",
            params={
                "location": location,
                "limit": 5
            }
        )
        
        data = response.json()
        print(f"  Found: {data.get('total_count', 0)} jobs")


def test_get_jobs_by_source():
    """Test filtering by source"""
    print_section("9. Get Jobs - Source Filter")
    
    sources = ["wuzzuf", "linkedin", "forasna"]
    
    for source in sources:
        print(f"\nSource: {source}")
        response = requests.get(
            f"{BASE_URL}/api/scraping/jobs",
            params={
                "source": source,
                "limit": 5
            }
        )
        
        data = response.json()
        print(f"  Found: {data.get('total_count', 0)} jobs")


def test_get_jobs_advanced():
    """Test advanced filtering with multiple criteria"""
    print_section("10. Get Jobs - Advanced Filtering")
    
    print("Test: Senior Python jobs in Cairo from last 7 days")
    response = requests.get(
        f"{BASE_URL}/api/scraping/jobs",
        params={
            "keyword": "python",
            "location": "Cairo",
            "experience_level": "Senior",
            "posted_within_days": 7,
            "limit": 10
        }
    )
    
    data = response.json()
    print(f"Status: {response.status_code}")
    print(f"Total Found: {data.get('total_count', 0)}")
    
    if data.get('data'):
        print(f"\nResults:")
        for job in data['data']:
            print(f"  - {job['title']} at {job['company_name']}")
            print(f"    Level: {job['experience_level']}")
            print(f"    Skills: {', '.join(job['skills'][:5])}")
            print()


def test_job_statistics():
    """Test getting job statistics"""
    print_section("11. Job Statistics")
    
    response = requests.get(f"{BASE_URL}/api/scraping/jobs/stats")
    
    print(f"Status: {response.status_code}")
    data = response.json()
    
    if data.get('success'):
        stats = data['data']
        print(f"Total Jobs: {stats['total_jobs']}")
        
        print(f"\nBy Source:")
        for source, count in stats['by_source'].items():
            print(f"  {source}: {count}")
        
        print(f"\nTop 5 Categories:")
        sorted_cats = sorted(
            stats['by_category'].items(),
            key=lambda x: x[1],
            reverse=True
        )[:5]
        for cat, count in sorted_cats:
            print(f"  {cat}: {count}")
        
        print(f"\nTop Locations:")
        sorted_locs = sorted(
            stats['by_location'].items(),
            key=lambda x: x[1],
            reverse=True
        )[:5]
        for loc, count in sorted_locs:
            print(f"  {loc}: {count}")


def test_create_job_embedding():
    """Test creating a job embedding"""
    print_section("12. Create Job Embedding")
    
    payload = {
        "job_id": 12345,
        "job_data": {
            "title": "Senior Full Stack Developer",
            "description": "We are looking for an experienced full stack developer with expertise in React and Node.js. The ideal candidate will have 5+ years of experience building scalable web applications.",
            "requirements": "- 5+ years of web development experience\n- Strong knowledge of React and Node.js\n- Experience with PostgreSQL\n- Docker and Kubernetes experience preferred",
            "responsibilities": "- Design and implement new features\n- Collaborate with product team\n- Mentor junior developers\n- Conduct code reviews",
            "skills": ["React", "Node.js", "PostgreSQL", "Docker", "Kubernetes", "TypeScript"],
            "location": "Cairo, Egypt",
            "experience_level": "Senior",
            "employment_type": "Full-time",
            "company_name": "TechStartup Inc.",
            "category": "IT & Software Development"
        }
    }
    
    print("Request Payload:")
    print(json.dumps(payload, indent=2)[:500] + "...")
    
    response = requests.post(
        f"{BASE_URL}/api/embeddings/job",
        json=payload
    )
    
    print(f"\nStatus: {response.status_code}")
    print(f"Response: {json.dumps(response.json(), indent=2)}")


def run_all_tests():
    """Run all tests"""
    print("\n" + "="*60)
    print("  JOBLENS API TEST SUITE")
    print("="*60)
    
    try:
        # Basic tests
        test_health_check()
        test_get_categories()
        
        # Scraping tests
        job_id = test_trigger_scraping()
        if job_id:
            print("\n⏳ Waiting 5 seconds before checking status...")
            time.sleep(5)
            test_scraping_status(job_id)
        
        # Query tests
        test_get_jobs_basic()
        test_get_jobs_by_keyword()
        test_get_jobs_by_category()
        test_get_jobs_by_location()
        test_get_jobs_by_source()
        test_get_jobs_advanced()
        
        # Statistics
        test_job_statistics()
        
        # Embedding test
        test_create_job_embedding()
        
        print("\n" + "="*60)
        print("  ✅ ALL TESTS COMPLETED")
        print("="*60 + "\n")
        
    except requests.exceptions.ConnectionError:
        print("\n❌ ERROR: Could not connect to API server")
        print("Please make sure the server is running at http://127.0.0.1:8000")
        print("\nStart the server with: python api_server.py")
    
    except Exception as e:
        print(f"\n❌ ERROR: {str(e)}")


if __name__ == "__main__":
    run_all_tests()
