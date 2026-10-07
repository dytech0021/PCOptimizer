using System;
using System.Collections.Generic;
using System.Linq;
using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Trava as regras do otimizador de memória. A mais importante — nunca mexer no
/// jogo — mora em parte aqui (filtros de processo e de janela) e não pode falhar
/// em silêncio.
/// </summary>
public sealed class MemoryOptimizerPolicyTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private const ulong MiB = 1024UL * 1024;
    private static readonly DateTime Now = new(2026, 10, 7, 14, 32, 0, DateTimeKind.Utc);

    private static MemorySnapshot Snap(ulong total, ulong avail, ulong? free = null,
                                       ulong? standby = null, ulong? modified = null)
        => new(total, avail, free, standby, modified);

    // ── Plano ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MemoryTrigger.Batch)]
    [InlineData(MemoryTrigger.Tray)]
    [InlineData(MemoryTrigger.Auto)]
    public void AreasPesadasSoRodamNoBotaoManual(MemoryTrigger trigger)
    {
        // Uma otimização em lote ou automática não pode jogar fora todo o cache
        // de arquivos sem o usuário estar olhando.
        var plan = MemoryOptimizerPolicy.BuildPlan(MemoryOptimizerPolicy.All, trigger, true, true);

        Assert.Equal(MemoryOperation.None, plan.Ops & MemoryOptimizerPolicy.ManualOnly);
        Assert.Equal(MemoryOptimizerPolicy.ManualOnly, plan.SkippedByPolicy);
        Assert.True((plan.Ops & MemoryOperation.StandbyLowPriority) != 0);   // o leve continua
    }

    [Fact]
    public void ManualRespeitaAEscolhaDoUsuario()
    {
        var plan = MemoryOptimizerPolicy.BuildPlan(
            MemoryOperation.StandbyFull | MemoryOperation.ModifiedList, MemoryTrigger.Manual, true, true);

        Assert.Equal(MemoryOperation.StandbyFull | MemoryOperation.ModifiedList, plan.Ops);
    }

    [Fact]
    public void CacheCompletoDispensaOBaixaPrioridade()
    {
        var plan = MemoryOptimizerPolicy.BuildPlan(
            MemoryOperation.StandbyFull | MemoryOperation.StandbyLowPriority, MemoryTrigger.Manual, true, true);

        Assert.True((plan.Ops & MemoryOperation.StandbyFull) != 0);
        Assert.True((plan.Ops & MemoryOperation.StandbyLowPriority) == 0);
    }

    [Fact]
    public void SemPrivilegioLimpezaDeCacheViraPuladaNaoFalha()
    {
        var plan = MemoryOptimizerPolicy.BuildPlan(MemoryOptimizerPolicy.All, MemoryTrigger.Manual, false, false);

        Assert.Equal(MemoryOperation.TrimPrograms, plan.Ops);
        Assert.True((plan.SkippedNoAdmin & MemoryOperation.SystemFileCache) != 0);
        Assert.True((plan.SkippedNoAdmin & MemoryOperation.StandbyFull) != 0);
    }

    [Fact]
    public void BitsDesconhecidosDasConfiguracoesSaoIgnorados()
    {
        var plan = MemoryOptimizerPolicy.BuildPlan((MemoryOperation)0xFF, MemoryTrigger.Manual, true, true);

        Assert.Equal(MemoryOperation.None, plan.Ops & ~MemoryOptimizerPolicy.All);
    }

    [Fact]
    public void CachesVemAntesDosProgramas()
    {
        // Páginas tiradas dos programas devem cair num cache já limpo e
        // continuar em RAM, não ser descartadas logo em seguida.
        var steps = MemoryOptimizerPolicy.OrderedSteps(
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache |
            MemoryOperation.StandbyFull | MemoryOperation.ModifiedList);

        Assert.Equal(new[]
        {
            MemoryOperation.ModifiedList, MemoryOperation.StandbyFull,
            MemoryOperation.SystemFileCache, MemoryOperation.TrimPrograms,
        }, steps);
    }

    [Fact]
    public void PadraoNaoLigaAsAreasPesadas()
    {
        Assert.Equal(MemoryOperation.None, MemoryOptimizerPolicy.DefaultOps & MemoryOptimizerPolicy.ManualOnly);
    }

    // ── Automático ────────────────────────────────────────────────────────

    private static AutoInputs Auto(double usedPercent, bool enabled = true, bool game = false,
                                   bool requireIdle = false, bool idle = true, DateTime? lastRun = null,
                                   MemoryOperation ops = MemoryOptimizerPolicy.DefaultOps)
    {
        ulong total = 16 * GiB;
        ulong used = (ulong)(total * usedPercent / 100.0);
        return new AutoInputs(enabled, 85, Snap(total, total - used), game, requireIdle, idle,
                              Now, lastRun, TimeSpan.FromMinutes(30), ops);
    }

    [Fact]
    public void AutomaticoDesligadoNaoRoda() =>
        Assert.Equal(AutoSkipReason.Disabled, MemoryOptimizerPolicy.DecideAuto(Auto(95, enabled: false)).Reason);

    [Fact]
    public void ComJogoAbertoNaoRoda() =>
        Assert.Equal(AutoSkipReason.Game, MemoryOptimizerPolicy.DecideAuto(Auto(95, game: true)).Reason);

    [Fact]
    public void RespeitaOIntervaloMinimo() =>
        Assert.Equal(AutoSkipReason.Cooldown,
            MemoryOptimizerPolicy.DecideAuto(Auto(95, lastRun: Now.AddMinutes(-10))).Reason);

    [Fact]
    public void AbaixoDoLimiteNaoRoda() =>
        Assert.Equal(AutoSkipReason.BelowThreshold, MemoryOptimizerPolicy.DecideAuto(Auto(80)).Reason);

    [Fact]
    public void ExigindoOciosoEsperaOPcFicarOcioso() =>
        Assert.Equal(AutoSkipReason.NotIdle,
            MemoryOptimizerPolicy.DecideAuto(Auto(95, requireIdle: true, idle: false)).Reason);

    [Fact]
    public void NenhumaAreaMarcadaNaoTemOQueFazer() =>
        Assert.Equal(AutoSkipReason.NothingToDo,
            MemoryOptimizerPolicy.DecideAuto(Auto(95, ops: MemoryOperation.None)).Reason);

    [Fact]
    public void AcimaDoLimiteForaDoIntervaloRoda()
    {
        var d = MemoryOptimizerPolicy.DecideAuto(Auto(90, lastRun: Now.AddMinutes(-31)));
        Assert.True(d.Run);
    }

    [Fact]
    public void LeituraInvalidaNaoRoda()
    {
        var i = Auto(95) with { Snapshot = default };
        Assert.Equal(AutoSkipReason.NoReading, MemoryOptimizerPolicy.DecideAuto(i).Reason);
    }

    [Theory]
    [InlineData(30, 50)]
    [InlineData(52, 50)]
    [InlineData(72, 70)]
    [InlineData(83, 85)]
    [InlineData(99, 95)]
    public void LimiarEhArredondadoEPresoEntre50E95(int input, int expected) =>
        Assert.Equal(expected, MemoryOptimizerPolicy.ClampThreshold(input));

    [Fact]
    public void GanhoQueEvaporaNaoContaComoGanho()
    {
        // Esvaziou 2 GB; 10 min depois os programas puxaram 1,5 GB de volta.
        ulong total = 16 * GiB;
        Assert.False(MemoryOptimizerPolicy.GainHeld(14 * GiB, 12 * GiB, 13 * GiB + 512 * MiB, total, 85));
        // Puxaram só 500 MB e o uso ficou abaixo do limite: ganho real.
        Assert.True(MemoryOptimizerPolicy.GainHeld(14 * GiB, 12 * GiB, 12 * GiB + 500 * MiB, total, 85));
        // Ganho pequeno nunca conta.
        Assert.False(MemoryOptimizerPolicy.GainHeld(12 * GiB, 12 * GiB - 50 * MiB, 12 * GiB, total, 85));
    }

    [Fact]
    public void IntervaloDobraQuandoOGanhoNaoSeSustentaEVoltaQuandoSustenta()
    {
        var c = MemoryOptimizerPolicy.BaseCooldown;
        Assert.Equal(TimeSpan.FromMinutes(10), c);
        c = MemoryOptimizerPolicy.NextCooldown(c, productive: false);
        Assert.Equal(TimeSpan.FromMinutes(20), c);
        c = MemoryOptimizerPolicy.NextCooldown(c, false);
        c = MemoryOptimizerPolicy.NextCooldown(c, false);
        c = MemoryOptimizerPolicy.NextCooldown(c, false);
        Assert.Equal(MemoryOptimizerPolicy.MaxCooldown, c);
        Assert.Equal(MemoryOptimizerPolicy.BaseCooldown, MemoryOptimizerPolicy.NextCooldown(c, productive: true));
    }

    // ── Processos ─────────────────────────────────────────────────────────

    private static TrimContext Ctx(IEnumerable<int>? protectedPids = null, IEnumerable<string>? extra = null,
                                   IEnumerable<string>? dirs = null, IEnumerable<string>? user = null)
        => new(1000, new HashSet<int>(protectedPids ?? Array.Empty<int>()),
               (extra ?? Array.Empty<string>()).ToList(), (dirs ?? Array.Empty<string>()).ToList(),
               (user ?? Array.Empty<string>()).ToList(), @"C:\Windows");

    [Theory]
    [InlineData(4242, "chrome", 1, 300, true)]
    [InlineData(777, "chrome", 1, 300, false)]          // PID protegido (jogo/árvore da frente)
    [InlineData(1000, "PCOptimizer", 1, 300, false)]    // o próprio app
    [InlineData(4, "System", 0, 300, false)]
    [InlineData(4243, "svchost", 0, 300, false)]        // sessão 0
    [InlineData(4244, "AUDIODG", 1, 300, false)]
    [InlineData(4245, "EasyAntiCheat_EOS", 1, 300, false)]
    [InlineData(4246, "vmmem", 1, 300, false)]
    [InlineData(4247, "VoiceMeeterPro", 1, 300, false)] // prefixo
    [InlineData(4248, "Discord", 1, 900, false)]        // voz: estala se esvaziar
    [InlineData(4249, "Spotify", 1, 300, false)]
    [InlineData(4250, "steamwebhelper", 1, 10, false)]  // pequeno demais
    public void FiltroDeProcessosNuncaTocaNoQueNaoPode(int pid, string name, int session, int wsMb, bool expected)
    {
        var c = new ProcessCandidate(pid, name, session, wsMb * (long)MiB);
        Assert.Equal(expected, MemoryOptimizerPolicy.ShouldTrim(c, Ctx(protectedPids: new[] { 777 })));
    }

    [Fact]
    public void ListaDoUsuarioEJogoDoGameBoostSaoRespeitados()
    {
        var ctx = Ctx(extra: new[] { "dota2" }, user: new[] { "obsidian.exe" });
        Assert.False(MemoryOptimizerPolicy.ShouldTrim(new ProcessCandidate(5000, "dota2", 1, 4000 * (long)MiB), ctx));
        Assert.False(MemoryOptimizerPolicy.ShouldTrim(new ProcessCandidate(5001, "Obsidian", 1, 400 * (long)MiB), ctx));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\a.exe", true)]
    [InlineData(@"C:\Windows2\a.exe", false)]
    [InlineData(@"D:\Jogos\Foo\bin\launcher.exe", true)]     // pasta protegida
    [InlineData(@"D:\Jogos\FooBar\x.exe", false)]            // respeita o separador
    [InlineData(@"E:\SteamLibrary\steamapps\common\Dota 2\game\bin\win64\dota2.exe", true)]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\x.exe", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData(null, true)]                                  // caminho desconhecido: não mexe
    public void CaminhoDoWindowsOuDeJogoFicaDeFora(string? path, bool excluded) =>
        Assert.Equal(excluded, MemoryOptimizerPolicy.PathExcluded(path, Ctx(dirs: new[] { @"D:\Jogos\Foo" })));

    [Theory]
    [InlineData(@"C:\Apps\chrome.exe", "chrome", true)]
    [InlineData(@"C:\Apps\CHROME.EXE", "chrome", true)]
    [InlineData(@"C:\Jogos\dota2.exe", "chrome", false)]     // PID reaproveitado por outro processo
    [InlineData(null, "chrome", false)]
    public void NomeDoExecutavelPrecisaBaterComORetrato(string? path, string name, bool expected) =>
        Assert.Equal(expected, MemoryOptimizerPolicy.ImageNameMatches(path, name));

    [Fact]
    public void AlvosSaoOsMaioresELimitadosA40()
    {
        var all = Enumerable.Range(0, 60)
            .Select(i => new ProcessCandidate(2000 + i, "app" + i, 1, (100 + i) * (long)MiB));

        var picked = MemoryOptimizerPolicy.PickTrimTargets(all, Ctx());

        Assert.Equal(MemoryOptimizerPolicy.MaxTrimProcesses, picked.Count);
        Assert.Equal("app59", picked[0].Name);
        Assert.True(picked.Zip(picked.Skip(1)).All(p => p.First.WorkingSetBytes >= p.Second.WorkingSetBytes));
    }

    [Fact]
    public void ArvoreDeProcessosIncluiFilhosENetosMasNaoIrmaos()
    {
        // 10 (jogo) → 11 (renderizador) → 12 (CEF);  20 é outro programa.
        var procs = new[] { (10, 1), (11, 10), (12, 11), (20, 1), (21, 20) };
        var tree = MemoryOptimizerPolicy.ExpandProcessTree(procs, new[] { 10 });

        Assert.Equal(new HashSet<int> { 10, 11, 12 }, tree);
    }

    [Fact]
    public void ArvoreDeProcessosNaoEntraEmLoop()
    {
        // PID reaproveitado pode formar ciclo de "pais".
        var procs = new[] { (10, 11), (11, 10) };
        var tree = MemoryOptimizerPolicy.ExpandProcessTree(procs, new[] { 10 });

        Assert.Equal(new HashSet<int> { 10, 11 }, tree);
    }

    // ── Janelas ───────────────────────────────────────────────────────────

    private static readonly Rect32 Mon = new(0, 0, 1920, 1080);
    private static readonly Rect32 Mon2 = new(1920, 0, 5360, 1440);   // ultrawide à direita

    private static WindowFacts Win(Rect32 rect, Rect32? mon = null, long style = 0x80000000 /*WS_POPUP*/,
                                   long ex = 0, bool zoomed = false, bool minimized = false,
                                   string cls = "GenericGameWnd", string owner = "game", bool self = false,
                                   bool cloaked = false)
        => new(rect, mon ?? Mon, true, minimized, cloaked, zoomed, style, ex, cls, owner, self);

    [Fact]
    public void JanelaSemBordaDoTamanhoDoMonitorEhJogo() =>
        Assert.True(MemoryOptimizerPolicy.IsGameLikeWindow(Win(Mon)));

    [Fact]
    public void JogoNoSegundoMonitorTambemConta() =>
        Assert.True(MemoryOptimizerPolicy.IsGameLikeWindow(Win(Mon2, Mon2)));

    [Fact]
    public void JogoEmTelaCheiaMinimizadoContinuaAberto() =>
        // Minimizada: o serviço passa o tamanho RESTAURADO.
        Assert.True(MemoryOptimizerPolicy.IsGameLikeWindow(Win(new Rect32(0, 0, 1920, 1080), minimized: true)));

    [Fact]
    public void JanelaMaximizadaComumNaoEhJogo()
    {
        // Maximizada com moldura: GetWindowRect passa 8 px do monitor por causa
        // das bordas invisíveis — e mesmo assim não é jogo.
        const long overlapped = 0x00CF0000;   // WS_OVERLAPPEDWINDOW (inclui WS_CAPTION e WS_THICKFRAME)
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(
            Win(new Rect32(-8, -8, 1928, 1088), style: overlapped, zoomed: true, cls: "Chrome_WidgetWin_1", owner: "chrome")));
    }

    [Fact]
    public void JanelaComBarraDeTituloNaoEhTelaCheia() =>
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(Win(Mon, style: MemoryOptimizerPolicy.WS_CAPTION)));

    [Theory]
    [InlineData(0x00000020L)]   // transparente (sobreposição)
    [InlineData(0x00000080L)]   // janela de ferramenta
    [InlineData(0x08000000L)]   // sem ativação
    public void SobreposicoesNaoContamComoJogo(long exStyle) =>
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(Win(Mon, ex: exStyle)));

    [Theory]
    [InlineData("WorkerW", "explorer")]
    [InlineData("Progman", "explorer")]
    [InlineData("SomeClass", "nvcontainer")]
    [InlineData("SomeClass", "GameBar")]
    public void ShellEOverlaysDePlacaNaoContam(string cls, string owner) =>
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(Win(Mon, cls: cls, owner: owner)));

    [Fact]
    public void JanelaOcultaPeloDwmOuDoProprioAppNaoConta()
    {
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(Win(Mon, cloaked: true)));
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(Win(Mon, self: true)));
    }

    [Fact]
    public void JanelaPequenaNaoEhJogo() =>
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(Win(new Rect32(100, 100, 1380, 820))));

    [Fact]
    public void QualquerJanelaVisivelNaTelaProtegeODono()
    {
        // Um jogo em janela pequena que não se denunciou ainda fica protegido
        // enquanto estiver na tela; minimizado ou oculto, não.
        Assert.True(MemoryOptimizerPolicy.IsVisibleUserWindow(Win(new Rect32(100, 100, 954, 580))));
        Assert.False(MemoryOptimizerPolicy.IsVisibleUserWindow(Win(new Rect32(100, 100, 954, 580), minimized: true)));
        Assert.False(MemoryOptimizerPolicy.IsVisibleUserWindow(Win(new Rect32(100, 100, 954, 580), cloaked: true)));
        Assert.False(MemoryOptimizerPolicy.IsVisibleUserWindow(Win(new Rect32(0, 0, 40, 30))));
        Assert.False(MemoryOptimizerPolicy.IsVisibleUserWindow(Win(Mon, ex: MemoryOptimizerPolicy.WS_EX_TOOLWINDOW)));
    }

    [Fact]
    public void JanelaGrandeVisivelEhProtegidaPequenaNao()
    {
        Assert.True(MemoryOptimizerPolicy.IsBigVisibleWindow(Win(new Rect32(0, 0, 1600, 900))));
        Assert.False(MemoryOptimizerPolicy.IsBigVisibleWindow(Win(new Rect32(0, 0, 800, 600))));
    }

    // ── Leitura e textos ──────────────────────────────────────────────────

    [Fact]
    public void FotoSomaAsOitoPrioridadesDoCacheEmEspera()
    {
        var s = MemoryOptimizerPolicy.FromLists(16 * GiB, 6 * GiB,
            new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8 }, zeroPages: 10, freePages: 5,
            modifiedPages: 3, modifiedNoWritePages: 1, pageSize: 4096);

        Assert.Equal(36UL * 4096, s.StandbyBytes);
        Assert.Equal(15UL * 4096, s.FreeBytes);
        Assert.Equal(4UL * 4096, s.ModifiedBytes);
        Assert.True(s.HasLists);
    }

    [Fact]
    public void EmUsoComoOGerenciadorDeTarefas()
    {
        // Sem listas: total − disponível.  Com listas: também menos a modificada.
        Assert.Equal(10 * GiB, Snap(16 * GiB, 6 * GiB).InUseBytes);
        Assert.Equal(9 * GiB, Snap(16 * GiB, 6 * GiB, free: GiB, standby: 5 * GiB, modified: GiB).InUseBytes);
    }

    [Fact]
    public void SemListasNaoInventaCacheEmEspera()
    {
        var (_, detail) = MemoryOptimizerPolicy.DescribeSnapshot(Snap(16 * GiB, 6 * GiB), listsNeedAdmin: true);
        Assert.Contains("requer administrador", detail);
        Assert.DoesNotContain("livre", detail);
    }

    [Fact]
    public void FotoMostraUsoEDetalhe()
    {
        var (usage, detail) = MemoryOptimizerPolicy.DescribeSnapshot(
            Snap(16 * GiB, 6 * GiB, free: 2 * GiB, standby: 4 * GiB, modified: 0), false);

        Assert.Equal("Em uso 63% · 10,0 GB de 16,0 GB", usage);
        Assert.Equal("Disponível 6,0 GB · livre 2,0 GB · cache em espera 4,0 GB", detail);
    }

    [Fact]
    public void ResultadoMostraAQuedaDoEmUso()
    {
        var before = Snap(16 * GiB, 4 * GiB);
        var after = Snap(16 * GiB, 5 * GiB);

        string text = MemoryOptimizerPolicy.DescribeResult(MemoryRunOutcome.Done, before, after,
            MemoryOperation.None, MemoryOperation.None, new DateTime(2026, 10, 7, 14, 32, 0));

        Assert.Equal("Em uso 75% → 69% (−1,0 GB) · 14:32", text);
    }

    [Fact]
    public void EsvaziarCacheNaoSeVendeComoRamLiberada()
    {
        var before = Snap(16 * GiB, 6 * GiB, free: GiB, standby: 5 * GiB, modified: 0);
        var after = Snap(16 * GiB, 6 * GiB, free: 3 * GiB, standby: 3 * GiB, modified: 0);

        string text = MemoryOptimizerPolicy.DescribeResult(MemoryRunOutcome.Done, before, after,
            MemoryOperation.None, MemoryOperation.None, Now);

        Assert.Contains("livre: +2,0 GB", text);
        Assert.Contains("o cache já contava como disponível", text);
        Assert.DoesNotContain("−2,0 GB", text);
    }

    [Fact]
    public void GanhoPequenoEhDitoComHonestidade()
    {
        var text = MemoryOptimizerPolicy.DescribeResult(MemoryRunOutcome.Done,
            Snap(16 * GiB, 6 * GiB), Snap(16 * GiB, 6 * GiB + 40 * MiB),
            MemoryOperation.None, MemoryOperation.None, Now);

        Assert.Contains("Pouco a liberar (−40 MB)", text);
    }

    [Fact]
    public void SemAdministradorNadaFoiAlterado()
    {
        var outcome = MemoryOptimizerPolicy.Classify(MemoryOperation.None, MemoryOperation.None,
            MemoryOperation.StandbyLowPriority, paused: false, busy: false);

        Assert.Equal(MemoryRunOutcome.NeedsAdmin, outcome);
        Assert.Equal("Requer administrador — nada foi alterado",
            MemoryOptimizerPolicy.DescribeResult(outcome, default, default,
                MemoryOperation.None, MemoryOperation.StandbyLowPriority, Now));
    }

    [Fact]
    public void JogoAbertoTemPrioridadeSobreTudo()
    {
        Assert.Equal(MemoryRunOutcome.Paused, MemoryOptimizerPolicy.Classify(
            MemoryOperation.TrimPrograms, MemoryOperation.None, MemoryOperation.None, paused: true, busy: false));
    }

    [Theory]
    [InlineData(0UL, "0 MB")]
    [InlineData(150UL * 1024 * 1024, "150 MB")]
    [InlineData(1024UL * 1024 * 1024, "1,0 GB")]
    [InlineData(1_395_864_371UL, "1,3 GB")]
    [InlineData(16UL * 1024 * 1024 * 1024, "16,0 GB")]
    public void BytesComVirgulaDoPortugues(ulong bytes, string expected) =>
        Assert.Equal(expected, MemoryOptimizerPolicy.FormatBytes(bytes));

    [Theory]
    [InlineData(69.9, MemoryGaugeLevel.Normal)]
    [InlineData(70.0, MemoryGaugeLevel.Warning)]
    [InlineData(89.9, MemoryGaugeLevel.Warning)]
    [InlineData(90.0, MemoryGaugeLevel.Danger)]
    public void NivelDaBarra(double pct, MemoryGaugeLevel expected) =>
        Assert.Equal(expected, MemoryOptimizerPolicy.Level(pct));

    // ── PC ocioso ─────────────────────────────────────────────────────────

    [Fact]
    public void PrimeiraAmostraNaoContaComoOcioso()
    {
        var t = new CpuIdleTracker(10, TimeSpan.FromMinutes(5));
        t.Push(0, 0, 0, Now);
        Assert.False(t.IsIdle(Now));
    }

    [Fact]
    public void CincoMinutosAbaixoDe10PorCentoEhOcioso()
    {
        var t = new CpuIdleTracker(10, TimeSpan.FromMinutes(5));
        long idle = 0, kernel = 0;
        for (int i = 0; i <= 6; i++)
        {
            t.Push(idle, kernel, 0, Now.AddMinutes(i));
            idle += 980; kernel += 1000;   // 2% ocupado
        }
        Assert.True(t.IsIdle(Now.AddMinutes(6)));
    }

    [Fact]
    public void UmPicoZeraAContagem()
    {
        var t = new CpuIdleTracker(10, TimeSpan.FromMinutes(5));
        t.Push(0, 0, 0, Now);
        t.Push(980, 1000, 0, Now.AddMinutes(1));
        t.Push(980 + 600, 2000, 0, Now.AddMinutes(2));   // 40% ocupado
        t.Push(980 + 600 + 990, 3000, 0, Now.AddMinutes(3));

        // O pico zerou a contagem: só 1 minuto ocioso desde ele.
        Assert.False(t.IsIdle(Now.AddMinutes(3)));
    }

    // ── Modo agressivo ────────────────────────────────────────────────────

    [Fact]
    public void ModoAgressivoSomaAreasMasNuncaOCacheCompleto()
    {
        // O cache completo faria o próximo jogo carregar do disco: não entra.
        var plan = MemoryOptimizerPolicy.BuildPlan(MemoryOperation.None, MemoryTrigger.Manual, true, true, aggressive: true);

        Assert.Equal(MemoryOptimizerPolicy.AggressiveOps, plan.Ops);
        Assert.Equal(MemoryOperation.None, plan.Ops & MemoryOperation.StandbyFull);
    }

    [Fact]
    public void ModoAgressivoDeixaAMemoriaModificadaRodarNoAutomatico()
    {
        var plan = MemoryOptimizerPolicy.BuildPlan(MemoryOptimizerPolicy.All, MemoryTrigger.Auto, true, true, aggressive: true);

        Assert.True((plan.Ops & MemoryOperation.ModifiedList) != 0);
        Assert.Equal(MemoryOperation.StandbyFull, plan.SkippedByPolicy);   // o completo continua só no botão
    }

    [Fact]
    public void ModoAgressivoLimpaTodoProgramaAPartirDe16MB()
    {
        var all = Enumerable.Range(0, 100)
            .Select(i => new ProcessCandidate(3000 + i, "app" + i, 1, (20 + i) * (long)MiB));

        Assert.Equal(40, MemoryOptimizerPolicy.PickTrimTargets(all, Ctx()).Count);
        Assert.Equal(100, MemoryOptimizerPolicy.PickTrimTargets(all, Ctx(), aggressive: true).Count);
    }

    [Fact]
    public void SoAreasManuaisMarcadasTemRazaoPropria() =>
        Assert.Equal(AutoSkipReason.ManualOnlyChecked,
            MemoryOptimizerPolicy.DecideAuto(Auto(95, ops: MemoryOperation.StandbyFull)).Reason);

    // ── Raízes de árvore (bug do explorer) ───────────────────────────────

    [Fact]
    public void ExplorerNuncaViraRaizDaArvoreProtegida()
    {
        // O explorer é pai de quase tudo aberto pelo menu Iniciar: como raiz,
        // ele protegia todos os programas e a limpeza não fazia nada.
        var names = new Dictionary<int, string> { [100] = "explorer", [200] = "steam", [300] = "dwm" };

        var (tree, leaves) = MemoryOptimizerPolicy.SplitRoots(new[] { 100, 200, 300 }, names);

        Assert.Equal(new HashSet<int> { 200 }, tree);
        Assert.Equal(new HashSet<int> { 100, 300 }, leaves);
    }

    [Fact]
    public void FilhosDoExplorerContinuamLimpaveis()
    {
        var procs = new[] { (100, 1), (101, 100), (102, 100), (200, 100), (201, 200) };
        var names = new Dictionary<int, string> { [100] = "explorer", [200] = "steam" };
        var (tree, leaves) = MemoryOptimizerPolicy.SplitRoots(new[] { 100, 200 }, names);

        var protectedPids = MemoryOptimizerPolicy.ExpandProcessTree(procs, tree);
        protectedPids.UnionWith(leaves);

        Assert.Equal(new HashSet<int> { 100, 200, 201 }, protectedPids);   // 101 e 102 (abertos pelo explorer) ficam limpáveis
    }

    // ── Detecção de jogo (furos da revisão) ──────────────────────────────

    [Theory]
    [InlineData("UnityWndClass")]
    [InlineData("UnrealWindow")]
    [InlineData("SDL_app")]
    [InlineData("GLFW30")]
    public void ClasseDoMotorEhJogoMesmoEmJanelaComBarra(string cls) =>
        Assert.True(MemoryOptimizerPolicy.IsGameLikeWindow(
            Win(new Rect32(100, 100, 1380, 820), style: 0x00CF0000, cls: cls, owner: "algumjogo")));

    [Theory]
    [InlineData("RobloxPlayerBeta")]
    [InlineData("VALORANT-Win64-Shipping")]
    [InlineData("dota2.exe")]
    public void ExecutavelDeJogoConhecidoEhJogo(string owner)
    {
        Assert.True(MemoryOptimizerPolicy.IsKnownGameProcess(owner));
        Assert.True(MemoryOptimizerPolicy.IsGameLikeWindow(
            Win(new Rect32(0, 0, 854, 480), style: 0x00CF0000, cls: "WINDOWSCLIENT", owner: owner, minimized: true)));
    }

    [Fact]
    public void JogoMinimizadoEmResolucaoMenorQueOMonitorContinuaAberto()
    {
        // 1280×960 esticado num monitor 1920×1080: alt-tab minimiza e devolve a
        // resolução da área de trabalho. Antes, isso passava despercebido.
        Assert.True(MemoryOptimizerPolicy.IsGameLikeWindow(
            Win(new Rect32(0, 0, 1280, 960), minimized: true, cls: "GenericGameWnd", owner: "jogo")));
    }

    [Fact]
    public void AppSemBordaRedimensionavelMinimizadoNaoEhJogo()
    {
        // Discord/Spotify/Steam desenham a própria barra, mas são redimensionáveis.
        const long popupResizable = 0x80000000 | MemoryOptimizerPolicy.WS_THICKFRAME;
        Assert.False(MemoryOptimizerPolicy.IsGameLikeWindow(
            Win(new Rect32(0, 0, 1280, 800), style: popupResizable, minimized: true,
                cls: "Chrome_WidgetWin_1", owner: "Notion")));
    }

    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Dota 2\game\bin\win64\dota2.exe", true)]
    [InlineData(@"C:\XboxGames\Forza\Content\forza.exe", true)]
    [InlineData(@"C:\Program Files\Riot Games\VALORANT\live\VALORANT.exe", true)]
    [InlineData(@"C:\Program Files\Riot Games\Riot Client\RiotClientServices.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", false)]
    [InlineData(@"D:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe", false)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData(null, false)]
    public void PastaDeInstalacaoDeJogoSemOsLaunchers(string? path, bool expected) =>
        Assert.Equal(expected, MemoryOptimizerPolicy.IsGameInstallPath(path));

    [Theory]
    [InlineData("dota2", true)]
    [InlineData("chrome", false)]       // navegador em F11 pausa, mas não é lembrado
    [InlineData("AnyDesk", false)]
    [InlineData("explorer", false)]
    [InlineData("", false)]
    public void SoJogoEhLembradoAteFechar(string owner, bool expected) =>
        Assert.Equal(expected, MemoryOptimizerPolicy.CanLatchAsGame(owner));

    [Fact]
    public void PausaDizQuemBloqueou()
    {
        Assert.Equal("Pausado: dota2 aberto (jogo ou tela cheia) — nada foi alterado",
            MemoryOptimizerPolicy.PausedText("dota2.exe"));
        Assert.Equal(MemoryOptimizerPolicy.GamePausedText, MemoryOptimizerPolicy.PausedText(null));
    }

    [Fact]
    public void ResultadoAvisaOQueFicouParaOBotao()
    {
        string text = MemoryOptimizerPolicy.DescribeResult(MemoryRunOutcome.Done,
            Snap(16 * GiB, 4 * GiB), Snap(16 * GiB, 5 * GiB),
            MemoryOperation.None, MemoryOperation.None, MemoryOperation.StandbyFull, Now);

        Assert.Contains("cache em espera completo: só no botão", text);
    }

    [Fact]
    public void LoteSoComAreasManuaisExplicaPorQueNaoFezNada()
    {
        string text = MemoryOptimizerPolicy.DescribeResult(MemoryRunOutcome.NothingToDo, default, default,
            MemoryOperation.None, MemoryOperation.None, MemoryOperation.StandbyFull, Now);

        Assert.StartsWith("As áreas marcadas só rodam no botão", text);
    }

    [Fact]
    public void ContaDeOcupacaoUsaKernelComOcioso()
    {
        // GetSystemTimes: o tempo de kernel INCLUI o ocioso.
        var t = new CpuIdleTracker(10, TimeSpan.FromMinutes(5));
        t.Push(0, 0, 0, Now);
        t.Push(900, 1000, 100, Now.AddMinutes(1));

        Assert.Equal(100.0 * (1100 - 900) / 1100, t.LastBusyPercent!.Value, 3);
    }
}
