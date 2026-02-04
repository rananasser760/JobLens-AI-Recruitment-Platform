#!/usr/bin/env python3
"""
JobLens Quick Start Script
Automates initial setup and first scraping run
"""

import subprocess
import sys
import os
from pathlib import Path


def print_header(text):
    """Print formatted header"""
    print("\n" + "="*60)
    print(f"  {text}")
    print("="*60 + "\n")


def check_python_version():
    """Check Python version"""
    print_header("Checking Python Version")
    version = sys.version_info
    print(f"Python version: {version.major}.{version.minor}.{version.micro}")
    
    if version.major < 3 or (version.major == 3 and version.minor < 10):
        print("❌ ERROR: Python 3.10+ is required")
        print("Please upgrade Python and try again")
        sys.exit(1)
    
    print("✅ Python version is compatible")


def install_dependencies():
    """Install required packages"""
    print_header("Installing Dependencies")
    
    print("📦 Installing Python packages...")
    try:
        subprocess.check_call([
            sys.executable, "-m", "pip", "install", "-r", "requirements.txt"
        ])
        print("✅ Python packages installed successfully")
    except subprocess.CalledProcessError:
        print("❌ Failed to install Python packages")
        return False
    
    print("\n🌐 Installing Playwright browsers...")
    try:
        subprocess.check_call([
            sys.executable, "-m", "playwright", "install", "chromium"
        ])
        print("✅ Playwright browsers installed successfully")
    except subprocess.CalledProcessError:
        print("❌ Failed to install Playwright browsers")
        return False
    
    return True


def create_directories():
    """Create necessary directories"""
    print_header("Creating Directories")
    
    directories = ["joblens_db", "logs", "backups"]
    
    for directory in directories:
        Path(directory).mkdir(exist_ok=True)
        print(f"✅ Created: {directory}/")


def setup_config():
    """Setup configuration file"""
    print_header("Setting Up Configuration")
    
    if Path(".env").exists():
        print("⚠️  .env file already exists")
        response = input("Do you want to overwrite it? (y/N): ")
        if response.lower() != 'y':
            print("Skipping configuration setup")
            return
    
    if Path("config.env.example").exists():
        import shutil
        shutil.copy("config.env.example", ".env")
        print("✅ Created .env file from example")
        print("📝 You can edit .env to customize settings")
    else:
        print("⚠️  config.env.example not found, skipping")


def test_installation():
    """Test if installation is working"""
    print_header("Testing Installation")
    
    print("🧪 Testing imports...")
    try:
        import fastapi
        import chromadb
        import playwright
        import sentence_transformers
        print("✅ All core packages imported successfully")
        return True
    except ImportError as e:
        print(f"❌ Import error: {e}")
        return False


def run_initial_scrape():
    """Ask user if they want to run initial scraping"""
    print_header("Initial Scraping")
    
    print("Would you like to run an initial scraping job?")
    print("This will scrape a small sample of jobs to test the system.")
    print("(This may take 5-10 minutes)")
    
    response = input("\nRun initial scrape? (y/N): ")
    
    if response.lower() == 'y':
        print("\n🚀 Starting initial scraping...")
        print("This will scrape 2 categories with 2 pages each")
        
        try:
            # Create a simple scraping script
            scrape_script = """
import asyncio
from job_scraper_enhanced import run_category_based_scraper

async def main():
    await run_category_based_scraper(
        categories=["IT & Software Development", "Data & AI"],
        sources=["wuzzuf"],
        max_pages_per_category=2,
        get_details=True
    )

asyncio.run(main())
"""
            
            with open("_temp_scrape.py", "w") as f:
                f.write(scrape_script)
            
            subprocess.check_call([sys.executable, "_temp_scrape.py"])
            os.remove("_temp_scrape.py")
            
            print("\n✅ Initial scraping completed!")
            
        except Exception as e:
            print(f"\n❌ Scraping failed: {e}")
            print("You can try running scraping manually later")


def show_next_steps():
    """Show next steps to user"""
    print_header("Setup Complete! 🎉")
    
    print("Next Steps:")
    print("\n1. Start the API Server:")
    print("   python api_server.py")
    
    print("\n2. Access API Documentation:")
    print("   Open browser: http://127.0.0.1:8000/docs")
    
    print("\n3. Run Tests:")
    print("   python test_api.py")
    
    print("\n4. Trigger Scraping via API:")
    print("   curl -X POST http://127.0.0.1:8000/api/scraping/trigger \\")
    print('     -H "Content-Type: application/json" \\')
    print('     -d \'{"sources": ["wuzzuf"], "max_pages": 2}\'')
    
    print("\n5. Get Jobs:")
    print("   curl http://127.0.0.1:8000/api/scraping/jobs?limit=10")
    
    print("\n📚 Documentation:")
    print("   - README.md - Full documentation")
    print("   - DEPLOYMENT.md - Deployment guide")
    print("   - config.env.example - Configuration options")
    
    print("\n" + "="*60)


def main():
    """Main setup function"""
    print("\n" + "="*60)
    print("  🎯 JOBLENS QUICK START")
    print("  Category-Based Job Scraper for Egypt Market")
    print("="*60)
    
    # Check if we're in the right directory
    required_files = ["requirements.txt", "job_scraper_enhanced.py", "api_server.py"]
    missing_files = [f for f in required_files if not Path(f).exists()]
    
    if missing_files:
        print("\n❌ ERROR: Missing required files:")
        for f in missing_files:
            print(f"   - {f}")
        print("\nPlease ensure all files are in the current directory")
        sys.exit(1)
    
    try:
        # Step 1: Check Python version
        check_python_version()
        
        # Step 2: Install dependencies
        if not install_dependencies():
            print("\n❌ Setup failed during dependency installation")
            sys.exit(1)
        
        # Step 3: Create directories
        create_directories()
        
        # Step 4: Setup configuration
        setup_config()
        
        # Step 5: Test installation
        if not test_installation():
            print("\n❌ Setup failed during testing")
            sys.exit(1)
        
        # Step 6: Optional initial scrape
        run_initial_scrape()
        
        # Step 7: Show next steps
        show_next_steps()
        
    except KeyboardInterrupt:
        print("\n\n⚠️  Setup interrupted by user")
        sys.exit(1)
    except Exception as e:
        print(f"\n❌ Setup failed with error: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
