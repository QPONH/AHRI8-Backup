using System;

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// .NET Framework 4.8 没有内置 <c>IsExternalInit</c>；C# 9 的 <c>record</c> / <c>init</c>
    /// 需要这个标记类型才能编译。它不参与运行时，只是编译器约定。
    /// </summary>
    internal static class IsExternalInit
    {
    }
}

namespace AHRI8Backup
{
    /// <summary>
    /// .NET Framework 4.8 的 <c>string.Contains</c> 没有带 <c>StringComparison</c> 的重载
    /// （那是 .NET Core / netstandard2.1 才有的）。这里补一个扩展方法，调用处写法保持不变。
    /// </summary>
    internal static class StringCompat
    {
        public static bool Contains(this string text, string value, StringComparison comparison) =>
            text.IndexOf(value, comparison) >= 0;
    }
}
