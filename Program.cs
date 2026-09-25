using CrmLeadManagement.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

var rawConnectionString = builder.Configuration["DATABASE_URL"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(rawConnectionString))
{
    throw new InvalidOperationException(
        "A PostgreSQL connection string is required. Set DATABASE_URL or "
        + "ConnectionStrings__DefaultConnection.");
}

var connectionString = NormalizePostgreSqlConnectionString(rawConnectionString);
var connectionStringProvider = builder.Configuration["DATABASE_URL"] is not null
    ? "DATABASE_URL environment variable"
    : "ConnectionStrings__DefaultConnection configuration";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(
        connectionString,
        npgsqlOptions =>
        {
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null);
        });
});

// Allows static stores to access the current request's DbContext.
builder.Services.AddHttpContextAccessor();

builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            ReferenceHandler.IgnoreCycles;

        options.JsonSerializerOptions.PropertyNamingPolicy = null;
    });

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var allowedOrigins = builder.Configuration["CORS_ALLOWED_ORIGINS"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (allowedOrigins is { Length: > 0 })
        {
            policy.WithOrigins(allowedOrigins);
        }
        else
        {
            policy.AllowAnyOrigin();
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc(
        "v1",
        new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "CRM Lead Management API",
            Version = "v1",
            Description =
                "JSON REST API for the Leads and Settings modules of the CRM Lead Management application."
        });

    options.CustomSchemaIds(type => type.FullName);

    var xmlFile =
        $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";

    var xmlPath = Path.Combine(
        AppContext.BaseDirectory,
        xmlFile);

    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

// Initialize DbAccessor.
DbAccessor.Initialize(
    app.Services.GetRequiredService<IHttpContextAccessor>());

// Apply EF Core migrations and seed initial data.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    app.Logger.LogInformation(
        "PostgreSQL configuration resolved from {Provider}",
        connectionStringProvider);

    db.Database.Migrate();

    LeadStore.SeedIfEmpty(db);
    SettingsStore.SeedIfEmpty(db);
}

// Safety-net save for MVC requests.
app.Use(async (context, next) =>
{
    await next();

    try
    {
        var db =
            context.RequestServices.GetRequiredService<AppDbContext>();

        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        var logger =
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("TrailingSaveChanges");

        logger.LogError(
            ex,
            "SaveChangesAsync failed after the response for {Method} {Path} had already started.",
            context.Request.Method,
            context.Request.Path);
    }
});

app.UseSwagger();

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint(
        "/swagger/v1/swagger.json",
        "CRM Lead Management API v1");
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.UseCors("Frontend");

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers();

app.Run();

static string NormalizePostgreSqlConnectionString(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var trimmedValue = value.Trim();

    if (Uri.TryCreate(trimmedValue, UriKind.Absolute, out var databaseUri)
        && (databaseUri.Scheme.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            || databaseUri.Scheme.Equals("postgresql", StringComparison.OrdinalIgnoreCase)))
    {
        var userInfo = databaseUri.UserInfo;
        var passwordSeparator = userInfo.IndexOf(':');
        var uriBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = databaseUri.Host,
            Database = databaseUri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(
                passwordSeparator >= 0 ? userInfo[..passwordSeparator] : userInfo)
        };

        if (passwordSeparator >= 0)
        {
            uriBuilder.Password = Uri.UnescapeDataString(
                userInfo[(passwordSeparator + 1)..]);
        }

        uriBuilder.SslMode = SslMode.Require;
        return uriBuilder.ConnectionString;
    }

    var connectionBuilder = new NpgsqlConnectionStringBuilder(trimmedValue);
    var host = connectionBuilder.Host;
    if (!string.IsNullOrWhiteSpace(host)
        && host.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
    {
        var hostUri = new Uri(host);
        connectionBuilder.Host = hostUri.Host;

        if (hostUri.Port > 0)
        {
            connectionBuilder.Port = hostUri.Port;
        }
    }

    return connectionBuilder.ConnectionString;
}
