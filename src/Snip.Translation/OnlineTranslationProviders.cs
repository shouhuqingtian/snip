using Snip.Core;

namespace Snip.Translation;

/// <summary>Suggestions for OpenAI-compatible Chat Completions. Account/model availability is provider-specific.</summary>
public static class OnlineTranslationProviders
{
    public const string DeepSeek = "DeepSeek", Qwen = "通义千问", OpenAI = "OpenAI",
        Glm = "智谱 GLM", Moonshot = "Moonshot / Kimi", SiliconFlow = "硅基流动";
    public const string CustomPrefix = "自定义：";
    public static string[] Names { get; } = [DeepSeek, Qwen, OpenAI, Glm, Moonshot, SiliconFlow];

    public static OnlineTranslationSettings Defaults(string provider) => provider switch
    {
        DeepSeek => new(provider, "https://api.deepseek.com/v1", "deepseek-chat"),
        Qwen => new(provider, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        OpenAI => new(provider, "https://api.openai.com/v1", "gpt-4.1-mini"),
        Glm => new(provider, "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        Moonshot => new(provider, "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        SiliconFlow => new(provider, "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
        _ => new(provider, "", "")
    };

    public static string[] VisionModels(string provider) => provider switch
    {
        Qwen => ["qwen-vl-plus", "qwen-vl-max"],
        OpenAI => ["gpt-4.1-mini", "gpt-4o-mini"],
        _ => []
    };

    public static string[] Models(string provider) => provider switch
    {
        DeepSeek => ["deepseek-chat", "deepseek-reasoner"],
        Qwen => ["qwen-plus", "qwen-flash", "qwen-turbo", "qwen-max"],
        OpenAI => ["gpt-4.1-mini", "gpt-4.1-nano", "gpt-4o-mini"],
        Glm => ["glm-4-flash", "glm-4-plus"],
        Moonshot => ["moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k"],
        SiliconFlow => ["Qwen/Qwen2.5-7B-Instruct", "Qwen/Qwen2.5-72B-Instruct"],
        _ => []
    };
}
