#!/bin/bash

echo "Setting up DoForYou API..."

# Check if PostgreSQL is running
if ! pg_isready -q; then
    echo "PostgreSQL is not running. Please start PostgreSQL first."
    exit 1
fi

# Create database if it doesn't exist
createdb doforyou 2>/dev/null || echo "Database 'doforyou' already exists"

# Install EF Core tools if not installed
if ! dotnet tool list -g | grep -q dotnet-ef; then
    echo "Installing Entity Framework Core tools..."
    dotnet tool install --global dotnet-ef
fi

# Restore packages
echo "Restoring NuGet packages..."
dotnet restore

# Create and apply migrations
echo "Creating database migrations..."
dotnet ef migrations add InitialCreate --force

echo "Applying migrations to database..."
dotnet ef database update

echo "Setup complete! You can now run the API with: dotnet run"
echo "API will be available at: http://localhost:5001"
echo "Swagger UI will be available at: http://localhost:5001/swagger"
echo ""
echo "Default admin credentials:"
echo "Email: admin@doforyou.com"
echo "Password: admin123"