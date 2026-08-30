import os

def generate_repo_snapshot():
    # 1. Force the script to start EXACTLY where this Python file is located
    abs_start = os.path.dirname(os.path.abspath(__file__))
    
    # Extract the base folder name to create a dynamic output filename
    base_folder_name = os.path.basename(abs_start)
    output_filename = f"{base_folder_name}_context.md"
    output_file = os.path.join(abs_start, output_filename)
    
    # 2. Folders to completely skip
    ignore_dirs = {'.git', 'node_modules', '__pycache__', 'venv', '.angular', 'dist', 'build', 'assets', '.vscode', '.idea'}
    
    # 3. File types to explicitly capture
    valid_extensions = {'.py', '.ts', '.html', '.css', '.scss', '.json', '.yml', '.yaml', '.txt', '.md', '.ini', '.env', '.cs'}
    valid_files = {'Dockerfile', 'docker-compose.yml', 'requirements.txt', 'package.json', 'angular.json'}
    
    # 4. Files to skip (locks, dynamic output file, and this script itself)
    skip_content_files = {'package-lock.json', 'yarn.lock', output_filename, os.path.basename(__file__)}
    
    with open(output_file, 'w', encoding='utf-8') as f:
        # AI Preamble
        f.write("# Repository Snapshot\n\n")
        f.write("This document contains the directory structure and file contents of the project. It is formatted specifically for AI context ingestion.\n\n")
        
        # Write the Tree Structure
        f.write("## Directory Structure\n```text\n")
        for root, dirs, files in os.walk(abs_start):
            dirs[:] = [d for d in dirs if d not in ignore_dirs]
            
            # Calculate folder depth safely using relative paths
            rel_dir = os.path.relpath(root, abs_start)
            level = 0 if rel_dir == '.' else rel_dir.count(os.sep) + 1
            
            indent = '    ' * level
            folder_name = os.path.basename(root) if rel_dir != '.' else os.path.basename(abs_start)
            f.write(f"{indent}/{folder_name}\n")
            
            subindent = '    ' * (level + 1)
            for file in files:
                if file not in skip_content_files:
                    f.write(f"{subindent}- {file}\n")
        f.write("```\n\n")
        
        # Write the File Contents
        f.write("## File Contents\n\n")
        for root, dirs, files in os.walk(abs_start):
            dirs[:] = [d for d in dirs if d not in ignore_dirs]
            for file in files:
                ext = os.path.splitext(file)[1].lower()
                if (ext in valid_extensions or file in valid_files) and file not in skip_content_files:
                    filepath = os.path.join(root, file)
                    
                    # Normalize slashes for AI readability
                    relative_path = os.path.relpath(filepath, abs_start).replace('\\', '/')
                    lang = ext.strip('.') if ext else 'text'
                    
                    try:
                        with open(filepath, 'r', encoding='utf-8') as code_file:
                            content = code_file.read().strip()
                            # Use explicit markdown headers and codeblocks for perfect parsing
                            f.write(f"### `{relative_path}`\n")
                            f.write(f"```{lang}\n")
                            f.write(content)
                            f.write("\n```\n\n")
                    except Exception as e:
                        f.write(f"### `{relative_path}`\n")
                        f.write(f"> **Error reading file:** {e}\n\n")

    print(f"[SUCCESS] Snapshot saved to: {output_file}")

if __name__ == "__main__":
    generate_repo_snapshot()