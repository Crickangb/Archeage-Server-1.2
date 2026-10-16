using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace AAEmu.Login.Core.Services.ManagerAccounts;

public static class ManagerAccountEndpoints
{
    private sealed record BotAccountRequest(string? Action, string? Username, string? Password);
    private sealed record LoginRequest(string? Username, string? Password);

    public static bool Enabled(IConfiguration configuration) =>
        configuration.GetValue<bool>("PlayerBotsManager:Enabled");

    private static bool PrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }

    public static IPAddress BindAddress(IConfiguration configuration)
    {
        var host = configuration["PlayerBotsManager:Host"] ?? "127.0.0.1";
        if (!IPAddress.TryParse(host, out var address) ||
            !(IPAddress.IsLoopback(address) || PrivateLanAddress(address)))
            throw new InvalidOperationException("PlayerBotsManager:Host must be a loopback or private LAN IP address.");
        return address;
    }

    private static IPAddress[] AllowedClients(IConfiguration configuration)
    {
        var clients = configuration.GetSection("PlayerBotsManager:AllowedClientIps").Get<string[]>() ?? [];
        return clients.Select(value =>
        {
            if (!IPAddress.TryParse(value, out var address) || !PrivateLanAddress(address))
                throw new InvalidOperationException("PlayerBotsManager:AllowedClientIps must contain private IPv4 addresses.");
            return address;
        }).ToArray();
    }

    public static bool ClientAllowed(IPAddress? address, IConfiguration configuration)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address) ||
               !IPAddress.IsLoopback(BindAddress(configuration)) && AllowedClients(configuration).Contains(address);
    }

    public static void AddManagerAccounts(this IServiceCollection services, IConfiguration configuration)
    {
        if (!Enabled(configuration))
            return;
        var key = configuration["PlayerBotsManager:Key"];
        if (string.IsNullOrEmpty(key) || key.Length < 32)
            throw new InvalidOperationException("PlayerBotsManager:Key requires at least 32 characters.");
        var bindAddress = BindAddress(configuration);
        var allowedClients = AllowedClients(configuration);
        if (!IPAddress.IsLoopback(bindAddress) && allowedClients.Length == 0)
            throw new InvalidOperationException("PlayerBotsManager:AllowedClientIps is required for a LAN listener.");
        services.AddSingleton<IManagerAccountRepository, ManagerAccountRepository>();
        services.AddSingleton<ManagerAccountService>();
        services.AddSingleton<ManagerBotAccountProvisioner>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("manager-login", limiter =>
            {
                limiter.PermitLimit = 20;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
        });
    }

    public static void MapManagerAccounts(this WebApplication app)
    {
        if (!Enabled(app.Configuration))
            return;
        app.UseRateLimiter();
        var key = Encoding.UTF8.GetBytes(app.Configuration["PlayerBotsManager:Key"]!);
        bool Authorized(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            var ip = context.Connection.RemoteIpAddress;
            if (!ClientAllowed(ip, app.Configuration) || context.Request.Headers.ContainsKey("Origin"))
                return false;
            var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-PlayerBots-Account-Key"].ToString());
            return supplied.Length == key.Length && CryptographicOperations.FixedTimeEquals(supplied, key);
        }

        app.MapPost("/playerbots-manager/login", async (HttpContext context, ManagerAccountService accounts) =>
        {
            if (!Authorized(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!context.Request.HasJsonContentType() || context.Request.ContentLength > 4096)
                return Results.BadRequest();
            try
            {
                var request = await context.Request.ReadFromJsonAsync<LoginRequest>(context.RequestAborted);
                var identity = await accounts.AuthenticateAsync(request?.Username ?? "", request?.Password ?? "",
                    context.RequestAborted);
                return identity is null ? Results.Unauthorized() : Results.Ok(identity);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest();
            }
            catch (Exception)
            {
                // Do not return SQL errors, hashes, credentials, or exception details.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting("manager-login");

        app.MapPost("/playerbots-manager/bot-accounts", async (HttpContext context, ManagerAccountService accounts,
            IManagerAccountRepository repository, ManagerBotAccountProvisioner provisioner) =>
        {
            if (!Authorized(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!context.Request.HasJsonContentType() || context.Request.ContentLength is null or > 4096) return Results.BadRequest();
            try
            {
                var request = await context.Request.ReadFromJsonAsync<BotAccountRequest>(context.RequestAborted);
                if (request?.Action == "lookup" && !string.IsNullOrWhiteSpace(request.Username) && request.Username.Length <= 32)
                {
                    var account = await repository.FindByUsernameAsync(request.Username, context.RequestAborted);
                    var identity = account == null ? null : await accounts.GetIdentityAsync(account.Id, context.RequestAborted);
                    return identity == null ? Results.NotFound() : Results.Ok(identity);
                }
                if (request?.Action != "create") return Results.BadRequest();
                var created = await provisioner.ProvisionAsync(request.Username ?? "", request.Password ?? "", context.RequestAborted);
                return created == null ? Results.Conflict() : Results.Ok(created);
            }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
            catch (Exception) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireRateLimiting("manager-login");

        app.MapGet("/playerbots-manager/accounts/{accountId}",
            async (HttpContext context, string accountId, ManagerAccountService accounts) =>
            {
                if (!Authorized(context))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (!uint.TryParse(accountId, out var id) || id == 0)
                    return Results.BadRequest();
                try
                {
                    var identity = await accounts.GetIdentityAsync(id, context.RequestAborted);
                    return identity is null ? Results.Unauthorized() : Results.Ok(identity);
                }
                catch (Exception)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
