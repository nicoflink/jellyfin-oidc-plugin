using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.LocalLogin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class LocalLoginPolicyFilterTests
{
    private readonly PluginConfiguration _configuration = new()
    {
        PublicJellyfinUrl = "https://jellyfin.example.test/",
        IssuerUrl = "https://id.example.test",
        ClientId = "jellyfin",
        ClientSecret = "client-secret",
        LocalLoginPolicy = LocalLoginPolicy.Disabled,
    };

    private readonly RecordingLogger<LocalLoginPolicyFilter> _logger = new();

    private readonly FakeLocalNetwork _localNetwork = new();

    [Fact]
    public void DisabledRefusesAuthenticateByNameWithA403()
    {
        var result = Assert.IsType<ContentResult>(Filter("AuthenticateUserByName"));

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("Password sign-in is disabled on this server.", result.Content);
    }

    [Theory]
    [InlineData(LocalLoginPolicy.Allowed, true, false)]
    [InlineData(LocalLoginPolicy.Allowed, false, false)]
    [InlineData(LocalLoginPolicy.LocalNetworkOnly, true, false)]
    [InlineData(LocalLoginPolicy.LocalNetworkOnly, false, true)]
    [InlineData(LocalLoginPolicy.Disabled, true, true)]
    [InlineData(LocalLoginPolicy.Disabled, false, true)]
    public void RefusesLocalLoginByPolicyAndWhereTheRequestComesFrom(LocalLoginPolicy policy, bool local, bool refused)
    {
        _configuration.LocalLoginPolicy = policy;
        _localNetwork.Local = local;

        Assert.Equal(refused, Filter("AuthenticateUserByName") is not null);
    }

    [Theory]
    [InlineData("PublicJellyfinUrl")]
    [InlineData("IssuerUrl")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public void AllowsLocalLoginUntilThePluginIsFullyConfigured(string missing)
    {
        typeof(PluginConfiguration).GetProperty(missing)!.SetValue(_configuration, string.Empty);

        Assert.Null(Filter("AuthenticateUserByName"));
    }

    [Theory]
    [InlineData("AuthenticateUserByName")]
    [InlineData("AuthenticateUser")]
    public void RefusesBothLocalLoginActions(string action)
    {
        Assert.NotNull(Filter(action));
    }

    [Theory]
    [InlineData("User", "AuthenticateWithQuickConnect")]
    [InlineData("QuickConnect", "InitiateQuickConnect")]
    [InlineData("QuickConnect", "GetQuickConnectState")]
    [InlineData("QuickConnect", "AuthorizeQuickConnect")]
    [InlineData("User", "ForgotPassword")]
    [InlineData("User", "ForgotPasswordPin")]
    [InlineData("User", "UpdateUserPassword")]
    [InlineData("User", "GetCurrentUser")]
    [InlineData("OidcLogin", "Start")]
    [InlineData("OidcLogin", "Complete")]
    [InlineData("OidcLoginAdmin", "SaveConfiguration")]
    [InlineData("Other", "AuthenticateUserByName")]
    public void LetsEveryOtherActionThrough(string controller, string action)
    {
        Assert.Null(Filter(action, controller));
    }

    [Fact]
    public void RefusalIsTheSameWhateverTheCredentials()
    {
        var existing = Assert.IsType<ContentResult>(Filter("AuthenticateUserByName", httpContext: Login("{\"Username\":\"admin\",\"Pw\":\"correct\"}")));
        var unknown = Assert.IsType<ContentResult>(Filter("AuthenticateUserByName", httpContext: Login("{\"Username\":\"nobody\",\"Pw\":\"guess\"}")));
        var malformed = Assert.IsType<ContentResult>(Filter("AuthenticateUserByName", httpContext: Login("not json")));

        Assert.Equivalent(existing, unknown, strict: true);
        Assert.Equivalent(existing, malformed, strict: true);
    }

    [Fact]
    public void RefusalLeavesTheRequestBodyUnread()
    {
        var httpContext = Login("{\"Username\":\"admin\",\"Pw\":\"correct\"}");

        Filter("AuthenticateUserByName", httpContext: httpContext);

        Assert.Equal(0, httpContext.Request.Body.Position);
    }

    [Fact]
    public void LogsEachRefusalOnceWithTheRemoteAddressAndPolicyButNoCredentials()
    {
        _configuration.LocalLoginPolicy = LocalLoginPolicy.LocalNetworkOnly;
        var httpContext = Login("{\"Username\":\"admin\",\"Pw\":\"hunter2\"}");
        httpContext.Request.QueryString = new QueryString("?pw=hunter2");

        Filter("AuthenticateUser", httpContext: httpContext);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Local Login from 203.0.113.7 refused by the Local Login Policy LocalNetworkOnly", entry.Message);
        Assert.DoesNotContain("admin", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsNothingWhenLetThrough()
    {
        _configuration.LocalLoginPolicy = LocalLoginPolicy.Allowed;

        Filter("AuthenticateUserByName");

        Assert.Empty(_logger.Entries);
    }

    private static DefaultHttpContext Login(string body)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return httpContext;
    }

    private IActionResult? Filter(string action, string controller = "User", HttpContext? httpContext = null)
    {
        httpContext ??= new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress ??= IPAddress.Parse("203.0.113.7");
        var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
        var context = new ResourceExecutingContext(
            new ActionContext(httpContext, new RouteData(), descriptor),
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());

        new LocalLoginPolicyFilter(new LocalLoginPolicyCheck(() => _configuration, _localNetwork), _logger).OnResourceExecuting(context);
        return context.Result;
    }
}
