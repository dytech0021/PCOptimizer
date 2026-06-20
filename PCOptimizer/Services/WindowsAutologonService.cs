using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Configura o auto-login do Windows (entrar direto na área de trabalho sem
    /// pedir senha) em todas as versões do Windows 10 e 11, incluindo 25H2.
    ///
    /// Mecanismo:
    ///   1. DevicePasswordLessBuildVersion = 0  → reabilita login por senha
    ///      (o Win11 25H2 vem com a trava "somente Windows Hello" ligada, que
    ///      faz o Winlogon ignorar o auto-login em contas Microsoft).
    ///   2. AutoAdminLogon = 1, DefaultUserName, DefaultDomainName no registro.
    ///   3. Senha gravada como SEGREDO LSA criptografado ("DefaultPassword") —
    ///      é o método que o netplwiz e o Sysinternals Autologon usam e o ÚNICO
    ///      que funciona de forma confiável em CONTAS MICROSOFT. Gravar a senha
    ///      como texto simples no registro funciona só em contas locais.
    ///
    /// Requer privilégios de administrador.
    /// </summary>
    public static class WindowsAutologonService
    {
        private const string WinlogonPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

        // Trava do Windows 10 2004+/Windows 11: quando = 2 (padrão), força login
        // só por Windows Hello em contas Microsoft e ESCONDE a opção do netplwiz,
        // fazendo o Winlogon ignorar o AutoAdminLogon. Zerar (=0) reabilita o
        // login por senha e é o que faz o auto-login funcionar no Windows 11 25H2.
        private const string PasswordLessPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

        // Nome do segredo LSA onde o Winlogon procura a senha do auto-login.
        private const string AutologonPasswordSecret = "DefaultPassword";

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string username, string domain, string password,
            int logonType, int logonProvider, out IntPtr token);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int Logon32LogonInteractive = 2;
        private const int Logon32LogonNetwork     = 3;
        private const int Logon32ProviderDefault  = 0;

        // ── LSA (Local Security Authority) — armazenamento seguro de segredos ──

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint LsaOpenPolicy(
            IntPtr SystemName,
            ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
            uint DesiredAccess,
            out IntPtr PolicyHandle);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "LsaStorePrivateData")]
        private static extern uint LsaStorePrivateData_Set(
            IntPtr PolicyHandle, ref LSA_UNICODE_STRING KeyName, ref LSA_UNICODE_STRING PrivateData);

        // Mesma função com PrivateData = NULL → apaga o segredo.
        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "LsaStorePrivateData")]
        private static extern uint LsaStorePrivateData_Delete(
            IntPtr PolicyHandle, ref LSA_UNICODE_STRING KeyName, IntPtr PrivateData);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr PolicyHandle);

        private const uint POLICY_CREATE_SECRET = 0x00000020;

        private static LSA_UNICODE_STRING MakeLsaString(string s)
        {
            return new LSA_UNICODE_STRING
            {
                Buffer = Marshal.StringToHGlobalUni(s),
                Length = (ushort)(s.Length * sizeof(char)),
                MaximumLength = (ushort)((s.Length + 1) * sizeof(char))
            };
        }

        /// <summary>
        /// Grava (ou apaga, se password vazia) a senha do auto-login no cofre LSA,
        /// criptografada. É o método confiável para contas Microsoft.
        /// </summary>
        private static bool StorePasswordInLsa(string? password)
        {
            IntPtr policy = IntPtr.Zero;
            LSA_UNICODE_STRING keyName = default;
            LSA_UNICODE_STRING data = default;
            bool dataAllocated = false;
            try
            {
                var attrs = new LSA_OBJECT_ATTRIBUTES
                {
                    Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>()
                };
                uint open = LsaOpenPolicy(IntPtr.Zero, ref attrs, POLICY_CREATE_SECRET, out policy);
                if (open != 0)
                {
                    Logger.Warn($"LsaOpenPolicy falhou (status 0x{open:X8}) — sem admin?");
                    return false;
                }

                keyName = MakeLsaString(AutologonPasswordSecret);

                uint res;
                if (string.IsNullOrEmpty(password))
                {
                    res = LsaStorePrivateData_Delete(policy, ref keyName, IntPtr.Zero);
                }
                else
                {
                    data = MakeLsaString(password);
                    dataAllocated = true;
                    res = LsaStorePrivateData_Set(policy, ref keyName, ref data);
                }
                if (res != 0)
                    Logger.Warn($"LsaStorePrivateData retornou status 0x{res:X8}");
                return res == 0;
            }
            catch (Exception ex) { Logger.Error(ex, "StorePasswordInLsa"); return false; }
            finally
            {
                if (keyName.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(keyName.Buffer);
                if (dataAllocated && data.Buffer != IntPtr.Zero)
                {
                    // Zera a senha na memória antes de liberar.
                    for (int i = 0; i < data.Length; i++) Marshal.WriteByte(data.Buffer, i, 0);
                    Marshal.FreeHGlobal(data.Buffer);
                }
                if (policy != IntPtr.Zero) LsaClose(policy);
            }
        }

        // ── API pública ──────────────────────────────────────────────────────

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
                return (key?.GetValue("AutoAdminLogon") as string) == "1";
            }
            catch { return false; }
        }

        public static string GetConfiguredUser()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath);
                return (key?.GetValue("DefaultUserName") as string) ?? Environment.UserName;
            }
            catch { return Environment.UserName; }
        }

        /// <summary>
        /// Tenta validar as credenciais via LogonUser. É APENAS INFORMATIVO:
        /// retornar false não significa senha errada — contas Microsoft muitas
        /// vezes não validam offline. O chamador grava de qualquer forma.
        /// Tenta várias combinações de domínio/usuário/tipo para cobrir conta
        /// local e conta Microsoft.
        /// </summary>
        public static bool ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrEmpty(password)) return true; // sem senha: deixa passar

            // (usuário, domínio) — cobre conta local e conta Microsoft.
            var attempts = new (string user, string domain)[]
            {
                (username, Environment.MachineName),
                (username, "."),
                (username, ""),
                (username, "MicrosoftAccount"),         // contas Microsoft
                ("MicrosoftAccount\\" + username, ""),  // formato alternativo
            };

            foreach (var (user, domain) in attempts)
            foreach (int type in new[] { Logon32LogonInteractive, Logon32LogonNetwork })
            {
                IntPtr token = IntPtr.Zero;
                try
                {
                    if (LogonUser(user, domain, password, type, Logon32ProviderDefault, out token))
                        return true;
                }
                catch { }
                finally
                {
                    if (token != IntPtr.Zero) try { CloseHandle(token); } catch { }
                }
            }
            return false;
        }

        /// <summary>
        /// Reabilita o login por senha desativando a trava "somente Windows Hello"
        /// do Windows 11/10 2004+. Sem isto o AutoAdminLogon é ignorado em contas
        /// Microsoft. Cria a chave se não existir.
        /// </summary>
        private static void EnablePasswordLogin()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(PasswordLessPath, writable: true);
                key?.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
                Logger.Info("DevicePasswordLessBuildVersion=0 aplicado");
            }
            catch (Exception ex) { Logger.Error(ex, "EnablePasswordLogin (Win11 pode ignorar auto-login)"); }
        }

        /// <summary>
        /// Ativa o auto-login. Não valida internamente — chame ValidateCredentials antes.
        /// </summary>
        public static bool Enable(string username, string password)
        {
            try
            {
                // PASSO 1 (crítico no Win11 25H2): reabilita login por senha.
                EnablePasswordLogin();

                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, writable: true);
                if (key == null)
                {
                    Logger.Error("Enable: não abriu Winlogon p/ escrita — precisa de admin");
                    return false;
                }

                key.SetValue("AutoAdminLogon",    "1",                     RegistryValueKind.String);
                key.SetValue("DefaultUserName",   username,                RegistryValueKind.String);
                key.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);

                // PASSO 2: senha no cofre LSA (funciona em conta Microsoft).
                bool lsaOk = StorePasswordInLsa(password);
                Logger.Info($"Enable: usuário='{username}', LSA={(lsaOk ? "ok" : "falhou→texto")}");
                if (lsaOk)
                {
                    // Sucesso no LSA: remove a senha em texto do registro (mais
                    // seguro e evita conflito). É como o netplwiz/Sysinternals fazem.
                    try { key.DeleteValue("DefaultPassword", false); } catch { }
                }
                else
                {
                    // Fallback p/ contas locais ou se o LSA recusar: senha em texto.
                    if (!string.IsNullOrEmpty(password))
                        key.SetValue("DefaultPassword", password, RegistryValueKind.String);
                    else
                        try { key.DeleteValue("DefaultPassword", false); } catch { }
                }

                // Remove travas que fariam o auto-login valer só algumas vezes.
                try { key.DeleteValue("AutoLogonCount", false); } catch { }
                try { key.DeleteValue("ForceAutoLogon", false); } catch { }

                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "Enable"); return false; }
        }

        /// <summary>Desativa o auto-login e remove a senha (registro e cofre LSA).</summary>
        public static bool Disable()
        {
            try
            {
                StorePasswordInLsa(null); // apaga o segredo LSA
                using var key = Registry.LocalMachine.OpenSubKey(WinlogonPath, writable: true);
                if (key == null)
                {
                    Logger.Error("Disable: não abriu Winlogon p/ escrita — precisa de admin");
                    return false;
                }
                key.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                try { key.DeleteValue("DefaultPassword", false); } catch { }
                Logger.Info("Disable: auto-login desativado");
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "Disable"); return false; }
        }
    }
}
