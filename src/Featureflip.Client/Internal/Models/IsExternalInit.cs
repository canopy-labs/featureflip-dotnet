#if NETSTANDARD2_0
// Polyfill for init-only setters in netstandard2.0
// This allows using C# 9 init accessors while targeting older frameworks
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
#endif
