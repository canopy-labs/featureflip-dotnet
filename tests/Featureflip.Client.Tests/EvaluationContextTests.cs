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
        Assert.Equal("user-123", context.GetAttribute("UserId")); // case-insensitive
        Assert.Equal("alice@example.com", context.GetAttribute("email"));
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
