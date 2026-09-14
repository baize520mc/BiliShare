namespace BiliShare.Services;

/// <summary>
/// 服务端 API 业务异常：携带 HTTP 状态码、业务错误码与服务端 message，
/// 并根据错误码映射出可直接展示给用户的中文提示。
/// </summary>
public sealed class ApiException : Exception
{
    /// <summary>HTTP 状态码。</summary>
    public int HttpStatus { get; }

    /// <summary>业务错误码（响应体 code）。</summary>
    public int Code { get; }

    public ApiException(int httpStatus, int code, string message) : base(message)
    {
        HttpStatus = httpStatus;
        Code = code;
    }

    /// <summary>是否因访问令牌无效/过期导致（用于引导用户重新填写 PAT）。</summary>
    public bool IsTokenInvalid => HttpStatus == 401 || Code == 40101;

    /// <summary>可直接展示给用户的本地化提示。</summary>
    public string UserHint => BuildHint(HttpStatus, Code, Message);

    private static string BuildHint(int httpStatus, int code, string serverMessage)
    {
        // 86095 死结：服务端无法自动修复，引导用户重新登录并一次性重抓全部字段
        if (code == 50002 && serverMessage.Contains("86095", StringComparison.Ordinal))
            return "Cookie 续期失败：登录态已失效，请重新登录 B 站并在同一次会话中重新抓取全部字段";

        return (httpStatus, code) switch
        {
            (401, _) or (_, 40101) => "访问令牌无效或已过期，请重新填写 PAT",
            // 开关类失败：服务端返回可读文案（如「未开放客户端连接」），直接展示
            (403, 40302) => string.IsNullOrWhiteSpace(serverMessage) ? "插件开关未开启，请检查插件设置" : serverMessage,
            (403, 40301) or (403, _) => "无权限执行此操作",
            (404, 40402) => "尚未保存 Cookie，请先保存后再试",
            (404, _) => "资源不存在",
            (500, 50002) => $"Cookie 刷新失败：{serverMessage}",
            (500, _) => "服务器内部错误，请稍后重试",
            (400, _) => "请求参数有误，请检查后重试",
            _ => serverMessage,
        };
    }
}