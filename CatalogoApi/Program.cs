using CatalogoApi.Authentication;
using CatalogoApi.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Cargar variables de entorno desde .env si existe (evitando fallos si no existe)
var envPaths = new[]
{
    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
    Path.Combine(Directory.GetCurrentDirectory(), "CatalogoApi", ".env"),
    Path.Combine(AppContext.BaseDirectory, ".env")
};

foreach (var envPath in envPaths)
{
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.Load(envPath);
        break;
    }
}

// Add services to the container.
// La API responde con DTOs; IgnoreCycles queda como red de seguridad para no
// volver a fallar con JsonException si alguna entidad se serializa por error.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Render termina TLS en su proxy y envia X-Forwarded-Proto / X-Forwarded-For.
// Se limpian las redes conocidas porque las IPs del proxy de Render son dinamicas.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Helper para obtener configuración con prioridad: variable de entorno -> IConfiguration -> fallback
string GetConfig(string envVar, string configPath, string fallback)
{
    var val = Environment.GetEnvironmentVariable(envVar);
    if (!string.IsNullOrWhiteSpace(val)) return val;
    val = builder.Configuration[configPath];
    if (!string.IsNullOrWhiteSpace(val)) return val;
    return fallback;
}

// TIKTOK_CALLBACK_URL puede venir como URL completa o solo como ruta.
// El host real lo calcula el handler desde la petición (con ForwardedHeaders).
string NormalizeCallbackPath(string value)
{
    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
    {
        return uri.AbsolutePath;
    }

    return value.StartsWith('/') ? value : "/" + value;
}

var connectionString = GetConfig(
    "DB_CONNECTION_STRING",
    "ConnectionStrings:DefaultConnection",
    "Host=localhost;Port=5432;Database=catalogo_db;Username=postgres;Password=postgres");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Data Protection persistente para mantener compatibilidad de cookies entre reinicios e instancias
var defaultKeysPath = string.Equals(
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
    "true",
    StringComparison.OrdinalIgnoreCase)
    ? "/app/keys"
    : "./keys";

var keysPath = GetConfig("DATA_PROTECTION_KEYS_PATH", "DataProtection:KeysPath", defaultKeysPath);
Directory.CreateDirectory(keysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("CatalogoApi");

// CORS para permitir peticiones con credenciales desde el frontend separado (Nginx)
const string corsPolicyName = "FrontendCorsPolicy";
var corsOriginsRaw = GetConfig(
    "CORS_ORIGINS",
    "Cors:AllowedOrigins",
    "http://localhost:3000,http://127.0.0.1:3000,http://localhost:5174,http://127.0.0.1:5174");
var frontendUrlOrigin = GetConfig("FRONTEND_URL", "FrontendUrl", string.Empty);

var allowedOrigins = corsOriginsRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(origin => origin.TrimEnd('/'))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

if (Uri.TryCreate(frontendUrlOrigin, UriKind.Absolute, out var frontendUri))
{
    allowedOrigins.Add(frontendUri.GetLeftPart(UriPartial.Authority));
}

builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicyName, policy =>
    {
        policy.WithOrigins(allowedOrigins.ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Configuración de autenticación con cookies y OAuth 2.0 (6 proveedores)
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "CatalogoApi.Auth";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.HttpOnly = true;

    // Responder 401 Unauthorized para peticiones no autenticadas a /api/* en vez de redirección 302
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    };

    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    };
})
.AddTwitch("Twitch", options =>
{
    options.ClientId = GetConfig("TWITCH_CLIENT_ID", "Authentication:Twitch:ClientId", "dummy-twitch-client-id");
    options.ClientSecret = GetConfig("TWITCH_CLIENT_SECRET", "Authentication:Twitch:ClientSecret", "dummy-twitch-client-secret");
    options.CallbackPath = new PathString(GetConfig("TWITCH_CALLBACK_PATH", "Authentication:Twitch:CallbackPath", "/signin-twitch"));
    options.SaveTokens = true;
    options.Events.OnCreatingTicket = context =>
    {
        context.Identity?.AddClaim(new Claim("provider", "Twitch"));
        return Task.CompletedTask;
    };
})
.AddTwitter("Twitter", options =>
{
    options.ClientId = GetConfig("TWITTER_CLIENT_ID", "Authentication:X:ClientId", "dummy-twitter-client-id");
    options.ClientSecret = GetConfig("TWITTER_CLIENT_SECRET", "Authentication:X:ClientSecret", "dummy-twitter-client-secret");
    options.CallbackPath = new PathString(GetConfig("TWITTER_CALLBACK_PATH", "Authentication:X:CallbackPath", "/signin-twitter"));
    // OAuth 2.0 con endpoint de usuario básico sin requerir email
    options.UserInformationEndpoint = "https://api.twitter.com/2/users/me";
    options.SaveTokens = true;
    options.Events.OnCreatingTicket = context =>
    {
        context.Identity?.AddClaim(new Claim("provider", "Twitter"));
        return Task.CompletedTask;
    };
})
.AddDiscord("Discord", options =>
{
    options.ClientId = GetConfig("DISCORD_CLIENT_ID", "Authentication:Discord:ClientId", "dummy-discord-client-id");
    options.ClientSecret = GetConfig("DISCORD_CLIENT_SECRET", "Authentication:Discord:ClientSecret", "dummy-discord-client-secret");
    options.CallbackPath = new PathString(GetConfig("DISCORD_CALLBACK_PATH", "Authentication:Discord:CallbackPath", "/signin-discord"));
    options.Scope.Add("identify");
    options.Scope.Add("email");
    options.SaveTokens = true;
    options.Events.OnCreatingTicket = context =>
    {
        context.Identity?.AddClaim(new Claim("provider", "Discord"));
        return Task.CompletedTask;
    };
})
.AddSpotify("Spotify", options =>
{
    options.ClientId = GetConfig("SPOTIFY_CLIENT_ID", "Authentication:Spotify:ClientId", "dummy-spotify-client-id");
    options.ClientSecret = GetConfig("SPOTIFY_CLIENT_SECRET", "Authentication:Spotify:ClientSecret", "dummy-spotify-client-secret");
    options.CallbackPath = new PathString(GetConfig("SPOTIFY_CALLBACK_PATH", "Authentication:Spotify:CallbackPath", "/signin-spotify"));
    options.Scope.Add("user-read-email");
    options.SaveTokens = true;
    options.Events.OnCreatingTicket = context =>
    {
        context.Identity?.AddClaim(new Claim("provider", "Spotify"));
        return Task.CompletedTask;
    };
})
.AddDropbox("Dropbox", options =>
{
    options.ClientId = GetConfig("DROPBOX_CLIENT_ID", "Authentication:Dropbox:ClientId", "dummy-dropbox-client-id");
    options.ClientSecret = GetConfig("DROPBOX_CLIENT_SECRET", "Authentication:Dropbox:ClientSecret", "dummy-dropbox-client-secret");
    options.CallbackPath = new PathString(GetConfig("DROPBOX_CALLBACK_PATH", "Authentication:Dropbox:CallbackPath", "/signin-dropbox"));
    options.SaveTokens = true;
    options.Events.OnCreatingTicket = context =>
    {
        context.Identity?.AddClaim(new Claim("provider", "Dropbox"));
        return Task.CompletedTask;
    };
})
.AddSlack("Slack", options =>
{
    options.ClientId = GetConfig("SLACK_CLIENT_ID", "Authentication:Slack:ClientId", "dummy-slack-client-id");
    options.ClientSecret = GetConfig("SLACK_CLIENT_SECRET", "Authentication:Slack:ClientSecret", "dummy-slack-client-secret");
    options.CallbackPath = new PathString(GetConfig("SLACK_CALLBACK_PATH", "Authentication:Slack:CallbackPath", "/signin-slack"));
    options.Scope.Add("identity.basic");
    options.Scope.Add("identity.email");
    options.SaveTokens = true;
    options.Events.OnCreatingTicket = async context =>
    {
        context.Identity?.AddClaim(new Claim("provider", "Slack"));

        // En Slack OAuth v2, el user token viene en authed_user.access_token
        string? userToken = context.AccessToken;
        if (string.IsNullOrWhiteSpace(userToken) &&
            context.TokenResponse?.Response?.RootElement.TryGetProperty("authed_user", out var authedUser) == true &&
            authedUser.TryGetProperty("access_token", out var tokenProp))
        {
            userToken = tokenProp.GetString();
        }

        if (!string.IsNullOrWhiteSpace(userToken))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://slack.com/api/users.identity");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            using var res = await context.Backchannel.SendAsync(req, context.HttpContext.RequestAborted);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean() &&
                    doc.RootElement.TryGetProperty("user", out var userProp))
                {
                    var slackId = userProp.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var slackName = userProp.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    var slackEmail = userProp.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;

                    if (context.Identity != null)
                    {
                        foreach (var old in context.Identity.FindAll(ClaimTypes.NameIdentifier).ToList())
                            context.Identity.RemoveClaim(old);
                        foreach (var old in context.Identity.FindAll(ClaimTypes.Name).ToList())
                            context.Identity.RemoveClaim(old);

                        if (!string.IsNullOrWhiteSpace(slackId))
                            context.Identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, slackId));
                        if (!string.IsNullOrWhiteSpace(slackName))
                            context.Identity.AddClaim(new Claim(ClaimTypes.Name, slackName));
                        if (!string.IsNullOrWhiteSpace(slackEmail))
                            context.Identity.AddClaim(new Claim(ClaimTypes.Email, slackEmail));
                    }
                }
            }
        }
    };
})
.AddTikTok("TikTok", options =>
{
    var tiktokCallbackUrl = GetConfig("TIKTOK_CALLBACK_URL", "Authentication:TikTok:CallbackUrl", "/signin-tiktok");

    options.ClientId = GetConfig("TIKTOK_CLIENT_KEY", "Authentication:TikTok:ClientKey", "dummy-tiktok-client-key");
    options.ClientSecret = GetConfig("TIKTOK_CLIENT_SECRET", "Authentication:TikTok:ClientSecret", "dummy-tiktok-client-secret");
    options.CallbackPath = new PathString(NormalizeCallbackPath(tiktokCallbackUrl));
    options.CallbackUrl = Uri.TryCreate(tiktokCallbackUrl, UriKind.Absolute, out var callbackUri) &&
                          (callbackUri.Scheme == Uri.UriSchemeHttp || callbackUri.Scheme == Uri.UriSchemeHttps)
        ? callbackUri.AbsoluteUri
        : null;
    options.AllowedOpenId = GetConfig("ALLOWED_TIKTOK_OPEN_ID", "Authentication:TikTok:AllowedOpenId", string.Empty);
    options.SaveTokens = true;
    options.Events.OnCreatingTicket = context =>
    {
        context.Identity?.AddClaim(new Claim("provider", "TikTok"));
        return Task.CompletedTask;
    };
    options.Events.OnRemoteFailure = async context =>
    {
        if (context.Failure is TikTokAccessDeniedException denied)
        {
            // Si ya existía una sesión de TikTok para ese mismo open_id, se invalida.
            var currentOpenId = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (context.HttpContext.User.HasClaim("provider", "TikTok") &&
                string.Equals(currentOpenId, denied.OpenId, StringComparison.Ordinal))
            {
                await context.HttpContext.SignOutAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme);
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                "{\"error\":\"Acceso denegado\",\"detalle\":\"La cuenta de TikTok no está autorizada para iniciar sesión.\"}");
            context.HandleResponse();
        }
    };
});

HashSet<string> ParseCsvSet(string value) =>
    value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

var allowedProviders = ParseCsvSet(
    GetConfig("REPORTE_ALLOWED_PROVIDERS", "ReporteAbraham:AllowedProviders", "Discord,Spotify"));
var allowedUsers = ParseCsvSet(
    GetConfig("REPORTE_ALLOWED_USERS", "ReporteAbraham:AllowedUsers", string.Empty));

var allowedMarcoProviders = ParseCsvSet(
    GetConfig("REPORTE_MARCO_ALLOWED_PROVIDERS", "ReporteMarco:AllowedProviders", "Dropbox,Slack"));
var allowedMarcoUsers = ParseCsvSet(
    GetConfig("REPORTE_MARCO_ALLOWED_USERS", "ReporteMarco:AllowedUsers", string.Empty));

void AddReportePolicy(
    Microsoft.AspNetCore.Authorization.AuthorizationOptions authOptions,
    string policyName,
    HashSet<string> providers,
    HashSet<string> users)
{
    authOptions.AddPolicy(policyName, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var provider = context.User.FindFirst("provider")?.Value;
            if (!string.IsNullOrWhiteSpace(provider) && providers.Contains(provider))
            {
                return true;
            }

            if (users.Count > 0)
            {
                var userIdentifiers = new[]
                {
                    context.User.FindFirst(ClaimTypes.Name)?.Value,
                    context.User.FindFirst("name")?.Value,
                    context.User.FindFirst("username")?.Value,
                    context.User.FindFirst(ClaimTypes.Email)?.Value,
                    context.User.FindFirst("email")?.Value,
                    context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    context.User.FindFirst("sub")?.Value,
                    context.User.FindFirst("id")?.Value
                };

                if (userIdentifiers.Any(u => !string.IsNullOrWhiteSpace(u) && users.Contains(u)))
                {
                    return true;
                }
            }

            return false;
        });
    });
}

builder.Services.AddAuthorization(options =>
{
    AddReportePolicy(options, "ReporteAbrahamPolicy", allowedProviders, allowedUsers);
    AddReportePolicy(options, "ReporteMarcoPolicy", allowedMarcoProviders, allowedMarcoUsers);
});

var app = builder.Build();

// Debe ejecutarse antes de cualquier middleware que dependa del esquema/IP real
// (HTTPS redirection, cookies, autenticacion) para reconocer el proxy de Render.
app.UseForwardedHeaders();

// Aplica las migraciones pendientes en PostgreSQL al arrancar, con reintentos
// por si el servicio de base de datos tarda unos segundos en aceptar conexiones.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    const int maxIntentos = 5;

    for (var intento = 1; intento <= maxIntentos; intento++)
    {
        try
        {
            db.Database.Migrate();
            break;
        }
        catch (Exception ex) when (intento < maxIntentos)
        {
            logger.LogWarning(
                ex,
                "No se pudo conectar a PostgreSQL para aplicar migraciones (intento {Intento}/{MaxIntentos}). Reintentando en 2 segundos...",
                intento,
                maxIntentos);
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

app.MapOpenApi();
app.MapScalarApiReference();

app.UseHttpsRedirection();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(corsPolicyName);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
