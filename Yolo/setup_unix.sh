#!/bin/bash
echo "Creating virtual environment..."
python3 -m venv venv
echo "Activating virtual environment..."
source venv/bin/activate
echo "Upgrading pip..."
pip install --upgrade pip
echo "Installing required packages..."
pip install opencv-python>=4.7.0
pip install ultralytics>=8.0.0
pip install numpy>=1.24.0
pip install Pillow>=10.0.0
pip install matplotlib>=3.7.0
echo "All packages installed! Your environment is ready."
