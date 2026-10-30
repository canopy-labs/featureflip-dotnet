using Xunit;

namespace Featureflip.Client.Tests;

public class EvaluationContextTests
{
    [Fact]
    public void EvaluationContext_BuiltInProperties_WorkCorrectly()
    {
        var context = new EvaluationContext
        {
            UserId = "user-123",
            Email = "alice@example.com",
            Country = "US"
        };

        Assert.Equal("user-123", context.UserId);
        Assert.Equal("alice@example.com", context.Email);
        Assert.Equal("US", context.Country);
    }

    [Fact]
    public void EvaluationContext_GetAttribute_ReturnsBuiltInProperties()
    {
        var context = new EvaluationContext
        {
            UserId = "user-123",
            Email = "alice@example.com"
        };

        Assert.Equal("user-123", context.GetAttribute("userId"));
        Assert.Equal("user-123", context.GetAttribute("user_id"));
        Assert.Equal("alice@example.com", context.GetAttribute("email"));
    }

    [Theory]
    [InlineData("UserId")]
    [InlineData("USERID")]
    [InlineData("userid")]
    [InlineData("USER_ID")]
    [InlineData("User_Id")]
    [InlineData("Email")]
    [InlineData("EMAIL")]
    [InlineData("Country")]
    [InlineData("COUNTRY")]
    public void EvaluationContext_GetAttribute_MatchesBuiltInNamesExactly(string key)
    {
        // The built-in names are matched exactly, like the engine and every other SDK:
        // any other casing is an ordinary attribute, missing unless set under that key (#4609).
        var context = new EvaluationContext
        {
            UserId = "user-123",
            Email = "alice@example.com",
            Country = "US"
        };

        Assert.Null(context.GetAttribute(key));
        Assert.Equal("custom", context.Set(key, "custom").GetAttribute(key));
    }

    [Fact]
    public void EvaluationContext_Set_AddsCustomAttributes()
    {
        var context = new EvaluationContext { UserId = "user-123" }
            .Set("plan", "pro")
            .Set("beta_tester", true)
            .Set("age", 25);

        Assert.Equal("pro", context.GetAttribute("plan"));
        Assert.Equal(true, context.GetAttribute("beta_tester"));
        Assert.Equal(25, context.GetAttribute("age"));
    }

    [Fact]
    public void EvaluationContext_GetAttribute_MatchesCustomAttributeKeysExactly()
    {
        // Exact key match, like the engine and every other SDK (#4527).
        var context = new EvaluationContext { UserId = "user-123" }
            .Set("Plan", "pro");

        Assert.Equal("pro", context.GetAttribute("Plan"));
        Assert.Null(context.GetAttribute("plan"));
        Assert.Null(context.GetAttribute("PLAN"));
        Assert.Null(context.Copy().GetAttribute("plan"));
    }

    [Fact]
    public void EvaluationContext_Set_KeepsKeysDifferingOnlyInCaseDistinct()
    {
        var context = new EvaluationContext()
            .Set("plan", "free")
            .Set("Plan", "pro");

        Assert.Equal("free", context.GetAttribute("plan"));
        Assert.Equal("pro", context.GetAttribute("Plan"));
    }

    [Fact]
    public void EvaluationContext_GetAttribute_CustomCountryDoesNotShadowBuiltInByCase()
    {
        // "country" falls through to the built-in Country property, not to a custom "Country".
        var context = new EvaluationContext().Set("Country", "US");

        Assert.Null(context.GetAttribute("country"));
    }

    [Fact]
    public void EvaluationContext_Set_ReturnsSameInstance_ForFluent()
    {
        var context = new EvaluationContext();
        var result = context.Set("key", "value");

        Assert.Same(context, result);
    }

    [Fact]
    public void EvaluationContext_GetAttribute_ReturnsUserId_ForSnakeCaseKey()
    {
        var context = new EvaluationContext { UserId = "user-123" };

        // "user_id" (snake_case) should resolve to the built-in UserId property
        Assert.Equal("user-123", context.GetAttribute("user_id"));
    }

    [Fact]
    public void EvaluationContext_BuiltInUserId_TakesPrecedence_OverCustomAttribute_SnakeCase()
    {
        var context = new EvaluationContext { UserId = "built-in" }
            .Set("user_id", "custom-override");

        // Built-in UserId takes precedence (matches Go SDK behavior)
        Assert.Equal("built-in", context.GetAttribute("user_id"));
    }

    [Fact]
    public void EvaluationContext_GetAttribute_ReturnsNull_ForMissingAttribute()
    {
        var context = new EvaluationContext { UserId = "user-123" };

        Assert.Null(context.GetAttribute("nonexistent"));
    }

    [Fact]
    public void EvaluationContext_BuiltInUserId_TakesPrecedence_OverCustomAttribute()
    {
        var context = new EvaluationContext { UserId = "user-123" }
            .Set("userId", "custom-id");

        // Built-in UserId takes precedence (matches Go SDK behavior)
        Assert.Equal("user-123", context.GetAttribute("userId"));
    }
}
