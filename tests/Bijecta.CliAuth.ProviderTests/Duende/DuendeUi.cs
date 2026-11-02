using System.Net;
using System.Security.Cryptography;
using System.Text;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bijecta.CliAuth.ProviderTests.Duende;

// IdentityServer ships no pages. These are the smallest login, consent and device-verification pages the protocol
// needs, built on its interaction services. Test code only; the Playwright page steps in DuendeLoginPage target them.
internal static class DuendeUi
{
    internal const string LoginPath = "/account/login";
    internal const string ConsentPath = "/consent";
    internal const string DevicePath = "/device";
    internal const string DeviceDoneText = "Device answer recorded.";

    internal sealed record TestUser(string UserName, string Password, string SubjectId);

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(LoginPath, (HttpContext http) => Page("Sign in", $"""
            <form method="post">
              <input type="hidden" name="returnUrl" value="{Encode(http.Request.Query["ReturnUrl"])}">
              <label>User <input id="username" name="username"></label>
              <label>Password <input id="password" name="password" type="password"></label>
              <button id="login" type="submit">Sign in</button>
            </form>
            """));

        app.MapPost(LoginPath, async (HttpContext http, TestUser user, IIdentityServerInteractionService interaction) =>
        {
            var form = await http.Request.ReadFormAsync();
            var returnUrl = form["returnUrl"].ToString();
            if (!Matches(form["username"], user.UserName) || !Matches(form["password"], user.Password))
            {
                return Page("Sign in", "<p id=\"error\">Invalid credentials.</p>", StatusCodes.Status401Unauthorized);
            }
            // Only the protocol's own return URLs (authorize callback or the device page) are followed.
            if (!interaction.IsValidReturnUrl(returnUrl) && !returnUrl.StartsWith(DevicePath + "?", StringComparison.Ordinal))
            {
                return Results.BadRequest();
            }
            await http.SignInAsync(new IdentityServerUser(user.SubjectId) { DisplayName = user.UserName });
            return Results.LocalRedirect(returnUrl);
        });

        app.MapGet(ConsentPath, async (HttpContext http, IIdentityServerInteractionService interaction) =>
        {
            var returnUrl = http.Request.Query["returnUrl"].ToString();
            var context = await interaction.GetAuthorizationContextAsync(returnUrl, http.RequestAborted);
            return context is null ? Results.BadRequest() : ConsentForm(returnUrl, context.ValidatedResources.RawScopeValues);
        });

        app.MapPost(ConsentPath, async (HttpContext http, IIdentityServerInteractionService interaction) =>
        {
            var form = await http.Request.ReadFormAsync();
            var returnUrl = form["returnUrl"].ToString();
            var context = await interaction.GetAuthorizationContextAsync(returnUrl, http.RequestAborted);
            if (context is null)
            {
                return Results.BadRequest();
            }
            if (form["answer"] == "allow")
            {
                await interaction.GrantConsentAsync(context, new ConsentResponse
                {
                    ScopesValuesConsented = context.ValidatedResources.RawScopeValues,
                    RememberConsent = false,
                }, http.RequestAborted);
            }
            else
            {
                await interaction.DenyAuthorizationAsync(context, InteractionError.AccessDenied, http.RequestAborted);
            }
            return Results.Redirect(returnUrl);
        });

        app.MapGet(DevicePath, async (HttpContext http, IDeviceFlowInteractionService devices) =>
        {
            var userCode = http.Request.Query["userCode"].ToString();
            if (userCode.Length == 0)
            {
                return Page("Device", $"""
                    <form method="get" action="{DevicePath}">
                      <label>Code <input id="user-code" name="userCode"></label>
                      <button id="submit-code" type="submit">Continue</button>
                    </form>
                    """);
            }
            if (http.User.Identity?.IsAuthenticated != true)
            {
                var back = $"{DevicePath}?userCode={Uri.EscapeDataString(userCode)}";
                return Results.Redirect($"{LoginPath}?ReturnUrl={Uri.EscapeDataString(back)}");
            }
            var context = await devices.GetAuthorizationContextAsync(userCode, http.RequestAborted);
            return context is null
                ? Page("Device", "<p id=\"error\">Unknown or expired code.</p>", StatusCodes.Status400BadRequest)
                : ConsentForm(userCode, context.ValidatedResources.RawScopeValues, DevicePath, "userCode");
        });

        app.MapPost(DevicePath, async (HttpContext http, IDeviceFlowInteractionService devices) =>
        {
            if (http.User.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }
            var form = await http.Request.ReadFormAsync();
            var userCode = form["userCode"].ToString();
            var context = await devices.GetAuthorizationContextAsync(userCode, http.RequestAborted);
            if (context is null)
            {
                return Results.BadRequest();
            }
            var response = form["answer"] == "allow"
                ? new ConsentResponse { ScopesValuesConsented = context.ValidatedResources.RawScopeValues, RememberConsent = false }
                : new ConsentResponse { Error = InteractionError.AccessDenied };
            var result = await devices.HandleRequestAsync(userCode, response, http.RequestAborted);
            return result.IsError ? Results.BadRequest() : Page("Device", $"<p id=\"done\">{DeviceDoneText}</p>");
        });
    }

    private static IResult ConsentForm(string value, IEnumerable<string> scopes, string action = ConsentPath, string field = "returnUrl") =>
        Page("Consent", $"""
            <form method="post" action="{action}">
              <input type="hidden" name="{field}" value="{Encode(value)}">
              <p>Requested: {Encode(string.Join(' ', scopes))}</p>
              <button id="allow" name="answer" value="allow" type="submit">Allow</button>
              <button id="deny" name="answer" value="deny" type="submit">Deny</button>
            </form>
            """);

    private static IResult Page(string title, string body, int status = StatusCodes.Status200OK) =>
        Results.Content($"<!doctype html><html><head><title>{title}</title></head><body>{body}</body></html>",
            "text/html; charset=utf-8", Encoding.UTF8, status);

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static bool Matches(string? supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied ?? ""), Encoding.UTF8.GetBytes(expected));
}
