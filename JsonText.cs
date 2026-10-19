using System.Text;

namespace AHRI8Backup;

/// <summary>JSON 文本工具：JavaScriptSerializer 只输出紧凑格式，这里负责给人读的缩进。</summary>
internal static class JsonFormat
{
    /// <summary>给 JSON 加缩进（字符串内的括号/逗号不处理）。输出仍然合法，可被 JavaScriptSerializer 重新解析。</summary>
    public static string Pretty(string json)
    {
        var sb = new StringBuilder(json.Length + json.Length / 4);
        int indent = 0;
        bool inString = false, escaped = false;
        foreach (char c in json)
        {
            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; sb.Append(c); break;
                case '{':
                case '[':
                    sb.Append(c).AppendLine();
                    sb.Append(new string(' ', (++indent) * 2));
                    break;
                case '}':
                case ']':
                    sb.AppendLine();
                    sb.Append(new string(' ', System.Math.Max(0, --indent) * 2));
                    sb.Append(c);
                    break;
                case ',':
                    sb.Append(c).AppendLine();
                    sb.Append(new string(' ', indent * 2));
                    break;
                case ':':
                    sb.Append(": ");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
