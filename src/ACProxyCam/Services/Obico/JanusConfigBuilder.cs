// JanusConfigBuilder.cs - Generates Janus configuration files for streaming

using System.Text;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Builds Janus configuration files for WebRTC streaming.
/// </summary>
public static class JanusConfigBuilder
{
    public const int JANUS_WS_PORT = 17730;
    public const int JANUS_ADMIN_WS_PORT = 17731;

    private static string? _configDir;

    /// <summary>
    /// Get or create the Janus config directory.
    /// </summary>
    public static string GetConfigDir()
    {
        if (_configDir == null)
        {
            _configDir = Path.Combine(Path.GetTempPath(), "acproxycam-janus");
            Directory.CreateDirectory(_configDir);
        }
        return _configDir;
    }

    /// <summary>
    /// Build all Janus configuration files.
    /// </summary>
    public static void BuildConfigs(string authToken, int streamId, int mjpegDataPort)
    {
        var configDir = GetConfigDir();

        BuildMainConfig(configDir, authToken);
        BuildWebSocketTransportConfig(configDir);
        BuildStreamingPluginConfig(configDir, streamId, mjpegDataPort);
    }

    /// <summary>
    /// Build main janus.jcfg configuration.
    /// </summary>
    private static void BuildMainConfig(string configDir, string authToken)
    {
        var config = $@"
general: {{
    admin_secret = ""janusoverlord""
}}
nat: {{
    stun_server = ""stun.l.google.com""
    stun_port = 19302
    turn_server = ""turn.obico.io""
    turn_port = 80
    turn_type = ""tcp""
    turn_user = ""{authToken}""
    turn_pwd = ""{authToken}""
    ice_ignore_list = ""vmnet""
    ignore_unreachable_ice_server = true
}}
plugins: {{
    disable = ""libjanus_audiobridge.so,libjanus_echotest.so,libjanus_nosip.so,libjanus_sip.so,libjanus_textroom.so,libjanus_videoroom.so,libjanus_duktape.so,libjanus_lua.so,libjanus_recordplay.so,libjanus_videocall.so,libjanus_voicemail.so""
}}
transports: {{
    disable = ""libjanus_mqtt.so,libjanus_nanomsg.so,libjanus_pfunix.so,libjanus_rabbitmq.so,libjanus_http.so""
}}
events: {{
}}
";
        File.WriteAllText(Path.Combine(configDir, "janus.jcfg"), config);
    }

    /// <summary>
    /// Build WebSocket transport configuration.
    /// </summary>
    private static void BuildWebSocketTransportConfig(string configDir)
    {
        var config = $@"
general: {{
    json = ""indented""
    ws = true
    ws_port = {JANUS_WS_PORT}
    ws_ip = ""127.0.0.1""
    wss = false
    ws_logging = ""err,warn""
}}
admin: {{
    admin_ws = true
    admin_ws_port = {JANUS_ADMIN_WS_PORT}
    admin_ws_ip = ""127.0.0.1""
    admin_wss = false
}}
";
        File.WriteAllText(Path.Combine(configDir, "janus.transport.websockets.jcfg"), config);
    }

    /// <summary>
    /// Build streaming plugin configuration for MJPEG.
    /// </summary>
    private static void BuildStreamingPluginConfig(string configDir, int streamId, int mjpegDataPort)
    {
        var config = $@"
mjpeg-{streamId}: {{
    type = ""rtp""
    id = {streamId}
    description = ""mjpeg-data""
    audio = false
    video = false
    data = true
    dataport = {mjpegDataPort}
    datatype = ""binary""
    dataiface = ""127.0.0.1""
    databuffermsg = false
}}
";
        File.WriteAllText(Path.Combine(configDir, "janus.plugin.streaming.jcfg"), config);
    }

    /// <summary>
    /// Find system-installed Janus binary path.
    /// </summary>
    public static string? FindJanusBinary()
    {
        // Common paths for system-installed Janus
        var paths = new[]
        {
            "/usr/bin/janus",
            "/usr/local/bin/janus",
            "/opt/janus/bin/janus"
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
