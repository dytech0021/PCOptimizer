using System;
using System.Linq;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Banco de assinaturas embutido para detecção de junkware/PUP/adware.
    /// Casamento por substring (case-insensitive). Mantido em código para
    /// acompanhar o estilo do projeto (igual ao BloatwareRemover) e ir junto no .exe.
    /// </summary>
    public static class ThreatSignatures
    {
        /// <summary>
        /// Trechos de nome de PROGRAMAS instalados reconhecidos como toolbar,
        /// "otimizador" enganoso, hijacker de busca ou adware empacotado.
        /// </summary>
        public static readonly string[] PupPrograms =
        {
            "Conduit", "Ask Toolbar", "Ask.com", "Babylon", "WebDiscover",
            "Search Protect", "SearchProtect", "Sweet Page", "Delta Search",
            "MyWebSearch", "MyPC Backup", "PC Speedup", "Speedup Pro",
            "PC Optimizer Pro", "RegClean Pro", "Reimage", "OneSafe PC Cleaner",
            "Driver Booster", "DriverPack", "Driver Updater", "Slimware",
            "DriverToolkit", "Advanced SystemCare", "WinZip Driver Updater",
            "MyCleanPC", "MacKeeper", "Wajam", "DealPly", "Yontoo", "Vosteran",
            "Mindspark", "iLivid", "Linkury", "InstallCore", "OpenCandy",
            "Spigot", "Hao123", "Baidu", "2345", "Tencent PC", "WebCake",
            "Coupon", "ShopperPro", "Shopping Assistant", "PriceFountain",
            "Genieo", "Trovi", "SuperFish", "VOPackage", "Crossrider",
            "Amazon Assistant", "MyStart", "SmartBar", "Funmoods", "Iminent",
            "Booster", "PC Accelerate", "Total System Care", "WinThruster",
            "PC Cleaner", "Auslogics", "uTorrentie", "Free RAM", "Mega Backup"
        };

        /// <summary>
        /// Trechos no NOME ou no DADO (caminho) de entradas de inicialização suspeitas.
        /// </summary>
        public static readonly string[] StartupSignatures =
        {
            "Conduit", "SearchProtect", "DealPly", "Wajam", "Linkury",
            "Updater by", "WebDiscover", "Babylon", "Trovi", "Iminent",
            "Spigot", "PriceFountain", "Crossrider", "GoSave", "MediaPlayerV1",
            "ShopperPro", "BrowserAir", "OptimizerPro", "SpeedUpMyPC"
        };

        /// <summary>
        /// IDs (32 caracteres) de extensões Chrome/Edge conhecidas como adware/hijacker.
        /// Conservador de propósito: só IDs com reputação ruim documentada entram aqui,
        /// para não remover extensões legítimas por engano.
        /// </summary>
        public static readonly string[] AdwareExtensionIds =
        {
            "gomekmidlodglbbmalcneegieacbdmki", // "Particle / Awesome New Tab Page" (adware)
            "lmjnegcaeklhafolokijcfjliaokphfk", // hijacker de busca conhecido
            "jcdgjdiieiljkfkdcloehkohchhpekkn", // "Search Manager" hijacker
            "lklfbkdigihjaaeamncibechhgalldgl"  // injetor de cupom
        };

        /// <summary>Trechos no nome de TAREFAS AGENDADAS criadas por adware.</summary>
        public static readonly string[] TaskSignatures =
        {
            "Conduit", "SearchProtect", "DealPly", "Wajam", "Linkury",
            "WebDiscover", "Updater_", "OptProStart", "BrowserUpdate_",
            "SpeedUp", "DriverBooster", "DriverPack", "Reimage", "SlimCleaner"
        };

        /// <summary>Nomes de PROCESSOS (sem .exe) conhecidos como malware/adware ativos.</summary>
        public static readonly string[] MalwareProcesses =
        {
            "searchprotect", "conduit", "dealply", "wajam", "linkury",
            "browserair", "optimizerpro", "speedupmypc", "trovi", "iminent",
            "crossrider", "shopperpro", "pricefountain", "gosave",
            "xmrig", "minerd", "nscpucnminer", "cgminer", "phoenixminer",
            "winminer", "coinhive", "cryptonight"
        };

        public static string? MatchPup(string name) => Match(name, PupPrograms);
        public static string? MatchStartup(string text) => Match(text, StartupSignatures);
        public static string? MatchTask(string name) => Match(name, TaskSignatures);

        public static bool IsAdwareExtension(string extensionId) =>
            AdwareExtensionIds.Any(id =>
                string.Equals(id.Trim(), extensionId.Trim(), StringComparison.OrdinalIgnoreCase));

        public static bool IsMalwareProcess(string processName) =>
            MalwareProcesses.Any(p =>
                processName.Contains(p, StringComparison.OrdinalIgnoreCase));

        private static string? Match(string? text, string[] signatures)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var sig in signatures)
                if (text.Contains(sig, StringComparison.OrdinalIgnoreCase))
                    return sig;
            return null;
        }
    }
}
