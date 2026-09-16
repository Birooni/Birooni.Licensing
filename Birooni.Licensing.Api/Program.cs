using Birooni.Licensing.Api.Common;
using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// CORS for Cloudflare Pages (admin.birooni.com)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Birooni Licensing API",
        Version = "v1",
        Description = "Production-grade licensing, updates, and management service for Birooni Revit plugins and software."
    });

    // Add X-Admin-Key security definition in Swagger UI
    options.AddSecurityDefinition("AdminApiKey", new OpenApiSecurityScheme
    {
        Description = "Admin API Key header. Enter your X-Admin-Key to authorize admin endpoints.",
        Name = "X-Admin-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "AdminApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});

// 2. Configure Database connection (DATABASE_URL env var with fallback to ConnectionStrings:Database)
var connectionString = ConnectionStringHelper.ResolveConnectionString(builder.Configuration);

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "PostgreSQL connection string was not found. Please provide DATABASE_URL environment variable or ConnectionStrings:Database in appsettings.");
}

builder.Services.AddDbContext<LicensingDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorCodesToAdd: null);
    });
});

// 3. Register Domain and Cryptographic Services
builder.Services.AddSingleton<ITokenSigner, TokenSigner>();
builder.Services.AddScoped<ILicensingService, LicensingService>();
builder.Services.AddScoped<IUpdateService, UpdateService>();

var app = builder.Build();

// 4. Configure HTTP request pipeline.
app.UseCors();

if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Birooni Licensing API v1");
        c.RoutePrefix = "swagger";
    });
}

// Redirect root to swagger UI for developer convenience
app.MapGet("/", () => Results.Redirect("/swagger"));

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();

// Ensure database schema columns and tables exist in Supabase
await DatabaseInitializer.InitializeSchemaAsync(app.Services, app.Logger);

app.Run();

