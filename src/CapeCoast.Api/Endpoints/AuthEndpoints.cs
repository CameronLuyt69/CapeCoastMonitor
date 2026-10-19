using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace CapeCoast.Api.Endpoints;

public record LoginRequest(string? Username, string? Password);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/login", Login).RequireRateLimiting(RateLimits.Login)
            .WithSummary("Sign in as Viewer or Admin. Sets an HttpOnly cookie.");
        auth.MapPost("/logout", (Delegate)Logout).RequireRateLimiting(RateLimits.Public);
        auth.MapGet("/me", Me).RequireRateLimiting(RateLimits.Public);
    }

    private static async Task<IResult> Login(LoginRequest request, HttpContext http, IOptions<AuthOptions> options)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password)
            || request.Username.Length > 100 || request.Password.Length > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = new[] { "username and password are required" } });

        var role = RoleFor(request.Username, request.Password, options.Value);
        if (role is null) return Results.Json(new { error = "invalid username or password" }, statusCode: 401);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, request.Username),
            new Claim(ClaimTypes.Role, role),
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Ok(new { username = request.Username, role });
    }

    private static async Task<IResult> Logout(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true) return Results.Json(new { error = "not signed in" }, statusCode: 401);
        return Results.Ok(new { username = http.User.Identity.Name, role = http.User.FindFirstValue(ClaimTypes.Role) });
    }

    /// <summary>A role whose password isn't configured can't be signed in to.</summary>
    private static string? RoleFor(string username, string password, AuthOptions o)
    {
        if (Matches(username, password, o.AdminUsername, o.AdminPassword)) return Roles.Admin;
        if (Matches(username, password, o.ViewerUsername, o.ViewerPassword)) return Roles.Viewer;
        return null;
    }

    private static bool Matches(string username, string password, string? expectedUser, string? expectedPassword)
    {
        if (string.IsNullOrEmpty(expectedUser) || string.IsNullOrEmpty(expectedPassword)) return false;
        // the placeholder from .env.example is public knowledge, so it never counts as a real password
        if (expectedPassword == AuthOptions.PlaceholderPassword) return false;
        // hash first so the comparison is fixed-length and constant time whatever was typed
        var user = CryptographicOperations.FixedTimeEquals(Hash(username), Hash(expectedUser));
        var pass = CryptographicOperations.FixedTimeEquals(Hash(password), Hash(expectedPassword));
        return user & pass;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
