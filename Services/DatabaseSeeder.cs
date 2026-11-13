using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Services;

public static class DatabaseSeeder
{
    public static async System.Threading.Tasks.Task SeedAsync(AppDbContext context)
    {
        // Seed Categories
        if (!await context.Categories.AnyAsync())
        {
            var categories = new[]
            {
                new Category { Name = "Cleaning", Description = "House cleaning, office cleaning", Icon = "fas fa-broom", SortOrder = 1 },
                new Category { Name = "Handyman", Description = "Repairs, maintenance, installations", Icon = "fas fa-tools", SortOrder = 2 },
                new Category { Name = "Transportation", Description = "Moving, delivery, driving", Icon = "fas fa-truck", SortOrder = 3 },
                new Category { Name = "Tutoring", Description = "Academic help, lessons", Icon = "fas fa-graduation-cap", SortOrder = 4 },
                new Category { Name = "Gardening", Description = "Lawn care, landscaping", Icon = "fas fa-seedling", SortOrder = 5 },
                new Category { Name = "Pet Care", Description = "Pet sitting, walking, grooming", Icon = "fas fa-paw", SortOrder = 6 },
                new Category { Name = "Technology", Description = "Computer help, setup, repair", Icon = "fas fa-laptop", SortOrder = 7 },
                new Category { Name = "General", Description = "Other miscellaneous tasks", Icon = "fas fa-tasks", SortOrder = 8 }
            };

            context.Categories.AddRange(categories);
            await context.SaveChangesAsync();
        }

        // Administrative accounts are provisioned only through the explicit, audited bootstrap flow.
        // Never create a privileged account or password from source-controlled seed data.

        // Demo accounts must be provisioned through an explicit staging-only workflow.
        // Never seed passwords, contact details, identity numbers or privileged roles from source.
    }
}