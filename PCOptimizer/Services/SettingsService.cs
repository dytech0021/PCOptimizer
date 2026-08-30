using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PCOptimizer.Services
{
    public class PresetData
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public int Brightness { get; set; }
        public int Contrast { get; set; }
    }

    public class AppSettings
    {
        public string Theme { get; set; } = "Light";
        public uint HotkeyModifiers { get; set; } = 0x0006; // MOD_CONTROL | MOD_SHIFT
        public uint HotkeyVk { get; set; } = 0x42;          // 'B'
        public string HotkeyDisplay { get; set; } = "Ctrl+Shift+B";

        public PresetData Preset1 { get; set; } = new() { Name = "Noturno", Icon = "🌙", Brightness = 20, Contrast = 40 };
        public PresetData Preset2 { get; set; } = new() { Name = "Normal", Icon = "☀️", Brightness = 50, Contrast = 50 };
        public PresetData Preset3 { get; set; } = new() { Name = "Máximo", Icon = "🔆", Brightness = 100, Contrast = 80 };

        public bool NightLightEnabled { get; set; }
        public int NightLightIntensity { get; set; } = 40;

        // Barra de tarefas transparente (estilo TranslucentTB)
        public bool TaskbarTransparencyEnabled { get; set; }
        public string TaskbarMode { get; set; } = "Transparent"; // Transparent | Blur | Acrylic | Off
        public int TaskbarTintAlpha { get; set; }                // 0-255: escurecimento (tom preto)
        // 0-2: variação de renderização. Padrão = 1 (anti-barra-preta): várias
        // builds do Win11 tratam alpha 0 como preto opaco; o piso de alpha 1 é
        // imperceptível e não afeta o Win10.
        public int TaskbarVariation { get; set; } = 1;

        // Modo acesso remoto: true = topologia "somente tela principal" ativada
        // pelo app (mantém o botão de reativar visível mesmo após reiniciar)
        public bool MultiMonitorDisabled { get; set; }

        // Modo Acesso Remoto (botão único): estado geral + HDR anterior
        public bool RemoteModeActive { get; set; }
        public bool RemotePrevHdr { get; set; }
        // Posições (x,y;x,y) das telas que estavam com HDR ligado — religa só nelas
        public string RemoteHdrPositions { get; set; } = "";
        // Luz noturna NATIVA do Windows estava ligada ao entrar no modo remoto
        public bool RemotePrevWinNightLight { get; set; }
        // Telas que estavam com ACM/WCG ("cores automáticas") ligado
        public string RemoteWcgPositions { get; set; } = "";
        // Vigia: manter HDR/ACM desligados enquanto o modo remoto estiver ativo
        // (o Windows os religa sozinho a cada reconfiguração de vídeo)
        public bool RemoteEnforceHdrOff { get; set; }
        public bool RemoteEnforceAcmOff { get; set; }

        // Removida na v1.70 (o ciclo automático piscava a tela) — mantida só para
        // o app conseguir desligá-la em quem atualizou de uma versão antiga
        public bool KeepSdrMode { get; set; }

        // Removida na v1.72.1 (o vigia por tempo brigava com o Windows e piscava
        // a tela) — mantida só para o app conseguir desligá-la em quem atualizou
        public bool KeepHdrOff { get; set; }

        // Corrige a cor automaticamente quando o vídeo muda (é o que acontece ao
        // conectar o acesso remoto). Por EVENTO, não por varredura periódica.
        public bool AutoFixColorOnDisplayChange { get; set; }

        // Turbo de Jogo: confina os outros programas nos E-cores enquanto o jogo
        // roda, deixando os P-cores livres. Nunca toca no processo do jogo.
        public bool GameBoostEnabled { get; set; } = true;        // liga sozinho ao detectar jogo
        public bool GameBoostLowerPriority { get; set; } = true;  // além dos E-cores, prioridade baixa
        public bool GameBoostWarningShown { get; set; }
        public bool CompetitiveModeWarningShown { get; set; }
        // Programas que o usuário quer manter fora do confinamento
        public List<string> GameBoostUserProtected { get; set; } = new();

        // Sai da frente dos jogos: com um jogo em tela cheia, pausa os timers de
        // segundo plano e baixa a prioridade do processo. Ligado por padrão —
        // nada do que esses timers fazem é visível durante um jogo em tela cheia.
        public bool GameAwareMode { get; set; } = true;

        // Monitores desativados pelo app: device (\\.\DISPLAYn) →
        // "LARGURAxALTURAxHZxPOSXxPOSY" de antes, para a reativação devolver a
        // tela ao mesmo lugar
        public Dictionary<string, string> DisabledMonitors { get; set; } = new();

        // Modo 16:9 com barras pretas (jogos) por monitor ultrawide:
        // HardwareId → "LARGURAxALTURAxHZ" da resolução original, para reverter
        public Dictionary<string, string> GameArPrevMode { get; set; } = new();

        // Resolução 1080p para acesso remoto: guarda a nativa para reversão
        public bool RemoteResActive { get; set; }
        public int RemotePrevWidth { get; set; }
        public int RemotePrevHeight { get; set; }
        public int RemotePrevHz { get; set; }

        // Cor avançada (gamma ramp da GPU): gama, temperatura e ganho por canal
        public double GammaValue { get; set; } = 1.0;  // 0.5–2.5 (1.0 = neutro)
        public int ColorTempK { get; set; } = 6500;    // 2000–10000 K (6500 = neutro)
        // Saturação (Digital Vibrance da NVIDIA): 0 = neutro, 63 = máximo.
        // Não dá para fazer pela rampa de gama — ela não mistura os canais.
        public int Saturation { get; set; }
        public int GainR { get; set; } = 100;          // 25–100%
        public int GainG { get; set; } = 100;
        public int GainB { get; set; } = 100;

        // user-assigned names for monitors, keyed by HardwareId
        public Dictionary<string, string> MonitorAliases { get; set; } = new();
    }

    public static class SettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "settings.json");

        public static AppSettings Current { get; private set; } = new();

        // Save é chamado da UI e de threads de fundo (modo remoto, ligar/desligar
        // monitor). Sem trava, duas gravações simultâneas no mesmo arquivo, ou
        // serializar enquanto outra thread mexe nos dicionários, falham — e o
        // catch engolia isso, fazendo o usuário perder configurações em silêncio.
        private static readonly object _io = new();

        public static void Load()
        {
            lock (_io)
            {
                try
                {
                    if (!File.Exists(SettingsPath)) return;
                    var json = File.ReadAllText(SettingsPath);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    Current = JsonSerializer.Deserialize<AppSettings>(json, opts) ?? new();
                }
                catch { Current = new(); }
            }
        }

        public static void Save()
        {
            lock (_io)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                    string json = JsonSerializer.Serialize(Current,
                        new JsonSerializerOptions { WriteIndented = true });

                    // Grava em arquivo temporário e troca: uma queda no meio da
                    // escrita não deixa o arquivo de configurações truncado.
                    string tmp = SettingsPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, SettingsPath, overwrite: true);
                }
                catch (Exception ex) { Logger.Error(ex, "SettingsService.Save"); }
            }
        }
    }
}
