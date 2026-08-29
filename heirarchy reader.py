import os

def generate_repo_snapshot(startpath):
    # Determine exact absolute path to save the file reliably
    abs_start = os.path.abspath(startpath)
    output_file = os.path.join(abs_start, "joblens_full_context.md")
    
    # 1. Folders to completely skip (prevents massive lag)
    ignore_dirs = {'.git', 'node_modules', '__pycache__', 'venv', '.angular', 'dist', 'build', 'assets', '.vscode', '.idea'}
    
    # 2. File types we ACTUALLY want the contents of
    valid_extensions = {'.py', '.ts', '.html', '.css', '.scss', '.json', '.yml', '.yaml', '.txt', '.md', '.ini', '.env'}
    valid_files = {'Dockerfile', 'docker-compose.yml', 'requirements.txt', 'package.json', 'angular.json'}
    
    # 3. Files to explicitly skip reading (like huge lock files)
    skip_content_files = {'package-lock.json', 'yarn.lock', 'joblens_full_context.md'}
    
    with open(output_file, 'w', encoding='utf-8') as f:
        # Write the Tree Structure
        f.write("# Repository Structure\n```text\n")
        for root, dirs, files in os.walk(abs_start):
            dirs[:] = [d for d in dirs if d not in ignore_dirs]
            level = root.replace(abs_start, '').count(os.sep)
            indent = '    ' * level
            folder_name = os.path.basename(root) or "."
            f.write(f"{indent}📁 {folder_name}/\n")
            
            subindent = '    ' * (level + 1)
            for file in files:
                if file not in skip_content_files:
                    f.write(f"{subindent}📄 {file}\n")
        f.write("```\n\n")
        
        # Write the File Contents
        f.write("# File Contents\n\n")
        for root, dirs, files in os.walk(abs_start):
            dirs[:] = [d for d in dirs if d not in ignore_dirs]
            for file in files:
                ext = os.path.splitext(file)[1]
                if (ext in valid_extensions or file in valid_files) and file not in skip_content_files:
                    filepath = os.path.join(root, file)
                    try:
                        with open(filepath, 'r', encoding='utf-8') as code_file:
                            content = code_file.read()
                            relative_path = os.path.relpath(filepath, abs_start)
                            f.write(f"## {relative_path}\n")
                            f.write(f"```{ext.strip('.') if ext else 'text'}\n")
                            f.write(content)
                            f.write("\n```\n\n")
                    except Exception as e:
                        f.write(f"## {file}\n*Could not read file: {e}*\n\n")

    print(f"✅ Success! File saved directly to:\n{output_file}")

if __name__ == "__main__":
    generate_repo_snapshot('.')