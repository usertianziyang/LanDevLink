namespace LanDevLink.Services;

public static class TargetUrlValidator
{
    public static Uri Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("请输入本机服务地址。", nameof(value));
        }

        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("地址格式无效，请输入类似 http://localhost:5173 的地址。", nameof(value));
        }

        if (!uri.IsLoopback)
        {
            throw new ArgumentException("为避免成为任意网络代理，只能分享本机 localhost 服务。", nameof(value));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("目标地址不能包含账号、查询参数或片段。", nameof(value));
        }

        if (uri.AbsolutePath != "/")
        {
            throw new ArgumentException("首版仅支持服务根地址，请移除地址后的路径。", nameof(value));
        }

        return uri;
    }
}

