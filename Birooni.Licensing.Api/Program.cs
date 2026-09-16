using Birooni.Licensing.Api.Common;
using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Birooni Licensing API",
        Version = "v1",
        Description = "Production-grade licensing and cryptographic validation service for Birooni plugins and software."
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

var app = builder.Build();

// 4. Configure HTTP request pipeline.
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

app.Run();
