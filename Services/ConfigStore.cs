using System.Text.Json;
using Reframe.Core;

namespace Reframe.Services;

/// <summary>配置读写:%LOCALAPPDATA%\Reframe\config.json</summary>
public static class ConfigStore
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");

    public static string Path_ => Path.Combine(Dir, "config.json");

    /// <summary>
    /// 纯读取尝试:文件存在则读+反序列化,成功返回非 null 配置;
    /// 文件不存在 / 读失败 / 解析失败 / 反序列化出 null,一律返回 <c>null</c>。
    /// <para><b>无副作用</b>:不 quarantine、不落默认、不写盘。供热重载用——外部编辑器写到一半
    /// 触发的文件事件会读到半截 JSON,此时返回 null 让调用方<b>保留当前内存配置</b>,
    /// 绝不把运行中的好配置换成默认并写盘(见 <see cref="ConfigService"/>.Reload)。</para>
    /// </summary>
    public static AppConfig? TryLoad()
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            string json = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 首次启动加载(仅 <see cref="ConfigService"/> 初始化那一次走它):
    /// 读到合法配置即用;损坏 / 缺文件 → quarantine 留底后回落默认并落盘。
    /// <para>注意与 <see cref="TryLoad"/> 的语义区别:Load 会在损坏时<b>改名 + 写默认盘</b>
    /// (首启时这是对的:把坏文件留底,给用户一个干净可用的默认);TryLoad 则<b>纯读不动盘</b>
    /// (热重载时这是对的:写到一半的文件不该触发覆盖)。</para>
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                string json = File.ReadAllText(Path_);
                var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (cfg != null) return cfg;
                // 反序列化出 null(空文件/字面 "null"):当损坏处理,留抢救余地。
                QuarantineCorrupt();
            }
        }
        catch
        {
            // JSON 损坏 / 读失败:先把坏文件改名留底,再回落默认(避免默认盘直接覆盖掉可抢救的原文件)。
            QuarantineCorrupt();
        }

        var def = AppConfig.CreateDefault();
        try { Save(def); } catch { /* 首次落盘失败不致命:本次会话用内存默认 */ }
        return def;
    }

    /// <summary>
    /// 把损坏的 config.json 改名为 config.json.corrupt-yyyyMMddHHmmss(保留抢救余地),
    /// 之后调用方会落默认。改名本身失败(占用/权限)则静默放过,不阻断回落默认。
    /// </summary>
    private static void QuarantineCorrupt()
    {
        try
        {
            if (!File.Exists(Path_)) return;
            string bak = Path_ + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(Path_, bak, overwrite: true);
        }
        catch { /* 改名失败就放过:大不了下次 Save 覆盖 */ }
    }

    /// <summary>
    /// 原子写盘:先写 config.json.tmp 再 File.Move(tmp, path, overwrite:true),
    /// 避免写到一半被读到半截文件。失败向上抛(调用方 ConfigService.Save 负责让用户可见)。
    /// </summary>
    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig);

        // tmp 带 GUID 防同进程并发写互相踩(以及残留 tmp 撞名)。
        string tmp = Path_ + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path_, overwrite: true); // 同卷下原子替换
        }
        catch
        {
            // 失败时尽量清掉残留 tmp,异常照常上抛。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }
}
