#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Input;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using dx = SharpDX;
using d2d = SharpDX.Direct2D1;
using dw = SharpDX.DirectWrite;
#endregion

// =====================================================================
// ENUMS
// =====================================================================
public enum ModoConfirmacaoTopoFundo
{
    Somente_Pavio,
    Somente_Fechamento,
    Pavio_E_Fechamento
}

public enum PosicaoDashboard
{
    Livre_Arrastavel
}

public enum TipoSinal
{
    Seta,
    Bolinha,
    Triangulo,
    Diamante,
    Quadrado,
    Cruz,
    Estrela
}

public enum TipoMediaMovel
{
    SMA,
    EMA,
    HMA,
    WMA,
    VWAP
}

namespace NinjaTrader.NinjaScript.Indicators
{
    // ═══════════════════════════════════════════════════════════════════════════
    // CAMADA 1 — CONTEXTO E QUALIDADE DE MERCADO  (módulo aditivo, sem estado NT8)
    // ---------------------------------------------------------------------------
    // Responsabilidade única: dado um snapshot de mercado (ADX, EMA, ATR, preço),
    // classificar o REGIME e a QUALIDADE do mercado ANTES de qualquer sinal, e
    // responder "posso operar?" / "vale a pena?". Não emite sinais, não desenha,
    // não depende de séries NT8 — recebe números prontos e devolve um veredito.
    // Isso mantém baixo acoplamento e permite testar/evoluir isolado.
    // ═══════════════════════════════════════════════════════════════════════════

    public enum RegimeMercado
    {
        Indefinido,
        Tendencial,   // ADX alto + EMA inclinada + direção consistente
        Lateral,      // ADX baixo, preço oscilando em torno da EMA
        Explosivo,    // volatilidade muito acima da média + expansão
        Exausto,      // movimento longo perdendo força (ADX caindo forte)
        Volatil,      // ATR alto mas sem direção (ruído)
        Fraco         // volume/força insuficientes para operar
    }

    // Snapshot de entrada — tudo o que a Camada 1 precisa, sem tocar em séries NT8.
    public struct ContextoInput
    {
        public double Adx;            // ADX atual
        public double AdxAnterior;    // ADX da barra anterior (para "crescente")
        public double AdxMinimo;      // limiar configurado
        public bool   ExigirAdxCrescente;

        public double EmaAtual;
        public double EmaAnterior;    // para medir inclinação
        public double PrecoAtual;
        public double InclinacaoMinimaTicks; // inclinação mínima da EMA em ticks
        public double TickSize;

        public double Atr;            // ATR atual
        public double AtrMedia;       // média do ATR (baseline de volatilidade)
        public double DistanciaMaxEmaAtr; // distância máx preço→EMA em múltiplos de ATR

        public double VolumeAtual;
        public double VolumeMedia;
        public double VolumeRelativoMin; // ex.: 1.2 = 120% da média

        public int QualidadeMinima;   // 1..5 estrelas mínimas para liberar sinal
    }

    // Resultado — o veredito explicável da Camada 1.
    public struct ContextoResultado
    {
        public RegimeMercado Regime;
        public int Estrelas;              // 1..5 qualidade do mercado
        public string RegimeTexto;        // "Tendencial", "Lateral"...
        public string QualidadeTexto;     // "Excelente", "Bom"...
        public bool PodeOperar;           // veredito final (qualidade >= mínima e não bloqueado)
        public string MotivoBloqueio;     // vazio se liberado; senão, por que reprovou

        // sub-scores (0..100) — alimentam o painel e o score explicável da Camada futura
        public double ScoreTendencia;
        public double ScoreVolatilidade;
        public double ScoreForca;
        public double ScoreLiquidez;
        public bool   PrecoEsticado;      // true se preço longe demais da EMA
        public int    Direcao;            // +1 alta, -1 baixa, 0 sem direção
    }

    public static class ContextoMercado
    {
        // Ponto único de entrada. Puro: mesmos inputs → mesmo resultado (fácil de testar).
        public static ContextoResultado Avaliar(ContextoInput c)
        {
            var r = new ContextoResultado();

            // ── Direção e inclinação da EMA (normalizada em ticks) ──
            double inclinacaoTicks = c.TickSize > 0 ? (c.EmaAtual - c.EmaAnterior) / c.TickSize : 0;
            r.Direcao = inclinacaoTicks > c.InclinacaoMinimaTicks ? 1
                      : inclinacaoTicks < -c.InclinacaoMinimaTicks ? -1 : 0;

            // ── Distância preço→EMA em ATR (evita entradas esticadas) ──
            double distAtr = c.Atr > 0 ? Math.Abs(c.PrecoAtual - c.EmaAtual) / c.Atr : 0;
            r.PrecoEsticado = c.DistanciaMaxEmaAtr > 0 && distAtr > c.DistanciaMaxEmaAtr;

            // ── Sub-scores 0..100 ──
            // Tendência: quão forte e direcional (ADX acima do mínimo + EMA inclinada)
            double adxNorm = Clamp01((c.Adx - c.AdxMinimo) / 25.0); // 25 pts de folga = 100%
            double inclNorm = c.InclinacaoMinimaTicks > 0
                ? Clamp01(Math.Abs(inclinacaoTicks) / (c.InclinacaoMinimaTicks * 3.0))
                : Clamp01(Math.Abs(inclinacaoTicks) / 3.0);
            r.ScoreTendencia = (adxNorm * 0.6 + inclNorm * 0.4) * 100.0;

            // Volatilidade: ATR relativo à média — ideal é "saudável" (0.8–1.6×)
            double volRatio = c.AtrMedia > 0 ? c.Atr / c.AtrMedia : 1.0;
            r.ScoreVolatilidade = FaixaSaudavel(volRatio, 0.8, 1.6) * 100.0;

            // Força: ADX + ADX crescente
            double forcaBase = Clamp01((c.Adx - 15.0) / 25.0);
            double bonusCrescente = c.Adx > c.AdxAnterior ? 0.15 : 0.0;
            r.ScoreForca = Clamp01(forcaBase + bonusCrescente) * 100.0;

            // Liquidez: volume relativo à média
            double volRel = c.VolumeMedia > 0 ? c.VolumeAtual / c.VolumeMedia : 1.0;
            r.ScoreLiquidez = Clamp01(volRel / Math.Max(1.0, c.VolumeRelativoMin)) * 100.0;

            // ── Classificação do REGIME ──
            r.Regime = ClassificarRegime(c, volRatio, r.Direcao);
            r.RegimeTexto = RegimeParaTexto(r.Regime);

            // ── Qualidade geral (1..5 estrelas) ──
            double qualidade = (r.ScoreTendencia * 0.30 + r.ScoreForca * 0.30
                             + r.ScoreVolatilidade * 0.20 + r.ScoreLiquidez * 0.20);
            // penalidades por regime ruim
            if (r.Regime == RegimeMercado.Exausto)  qualidade *= 0.55;
            if (r.Regime == RegimeMercado.Volatil)  qualidade *= 0.65;
            if (r.Regime == RegimeMercado.Fraco)    qualidade *= 0.45;
            if (r.Regime == RegimeMercado.Lateral)  qualidade *= 0.75;

            r.Estrelas = EstrelasDeScore(qualidade);
            r.QualidadeTexto = QualidadeParaTexto(r.Estrelas);

            // ── Veredito final: posso operar? ──
            r.MotivoBloqueio = "";
            if (c.ExigirAdxCrescente && c.Adx <= c.AdxAnterior)
                r.MotivoBloqueio = "ADX n\u00E3o est\u00E1 crescente";
            else if (c.Adx < c.AdxMinimo)
                r.MotivoBloqueio = $"ADX {c.Adx:F0} < m\u00EDnimo {c.AdxMinimo:F0}";
            else if (r.PrecoEsticado)
                r.MotivoBloqueio = "Pre\u00E7o esticado da EMA";
            else if (c.VolumeRelativoMin > 0 && volRel < c.VolumeRelativoMin)
                r.MotivoBloqueio = "Volume abaixo do m\u00EDnimo";
            else if (r.Estrelas < c.QualidadeMinima)
                r.MotivoBloqueio = $"Qualidade {r.Estrelas}\u2605 < m\u00EDnimo {c.QualidadeMinima}\u2605";

            r.PodeOperar = string.IsNullOrEmpty(r.MotivoBloqueio);
            return r;
        }

        private static RegimeMercado ClassificarRegime(ContextoInput c, double volRatio, int direcao)
        {
            bool adxForte = c.Adx >= Math.Max(c.AdxMinimo, 20.0);
            bool adxCaindoForte = c.Adx < c.AdxAnterior - 2.0;
            bool volAlta = volRatio > 1.8;
            bool volBaixa = volRatio < 0.6;

            if (volAlta && direcao != 0 && adxForte) return RegimeMercado.Explosivo;
            if (volAlta && direcao == 0)             return RegimeMercado.Volatil;
            if (adxForte && direcao != 0 && adxCaindoForte) return RegimeMercado.Exausto;
            if (adxForte && direcao != 0)            return RegimeMercado.Tendencial;
            if (volBaixa || c.Adx < 15.0)            return RegimeMercado.Fraco;
            return RegimeMercado.Lateral;
        }

        // ── helpers puros ──
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // 1.0 dentro da faixa saudável, caindo linearmente para fora dela
        private static double FaixaSaudavel(double v, double lo, double hi)
        {
            if (v >= lo && v <= hi) return 1.0;
            if (v < lo) return Clamp01(v / lo);
            return Clamp01(1.0 - (v - hi) / hi);
        }

        private static int EstrelasDeScore(double s)
        {
            if (s >= 85) return 5;
            if (s >= 70) return 4;
            if (s >= 50) return 3;
            if (s >= 30) return 2;
            return 1;
        }

        private static string RegimeParaTexto(RegimeMercado r)
        {
            switch (r)
            {
                case RegimeMercado.Tendencial: return "Tendencial";
                case RegimeMercado.Lateral:    return "Lateral";
                case RegimeMercado.Explosivo:  return "Explosivo";
                case RegimeMercado.Exausto:    return "Exausto";
                case RegimeMercado.Volatil:    return "Vol\u00E1til";
                case RegimeMercado.Fraco:      return "Fraco";
                default:                       return "Indefinido";
            }
        }

        private static string QualidadeParaTexto(int estrelas)
        {
            switch (estrelas)
            {
                case 5: return "Mercado Excelente";
                case 4: return "Bom";
                case 3: return "Neutro";
                case 2: return "Ruim";
                default: return "Evite Operar";
            }
        }

        // Utilitário de UI: string de estrelas cheias/vazias
        public static string Estrelas(int n)
        {
            n = n < 0 ? 0 : (n > 5 ? 5 : n);
            return new string('\u2605', n) + new string('\u2606', 5 - n);
        }
    }

    // Estado global do dashboard (classe própria para evitar colisão de nome com o
    // método gerado SINAIS(...)). Compartilhado entre dashboard e janela flutuante.
    public class DashboardEstado
    {
        // Modo de exibição: true = completo (Full), false = básico (leve/rápido).
        public bool ModoFull = true;
        // Reduz drasticamente a carga de animações (partículas) — ajuda em máquinas mais fracas.
        public bool AnimacoesLeves = false;
        // "Sinal 2.0": quando ligado, ativa as confluências avançadas (delta sintético +
        // bipolaridades S/R + cruzamento estocástico K→D) por cima dos sinais atuais.
        public bool Sinal20 = false;
        // Marca quando o modo Sinal 2.0 foi alternado, para o indicador limpar os sinais
        // do modo anterior e reprocessar (sinal a sinal um modo por vez).
        public bool Sinal20Mudou = false;
        public bool Sinal30 = false;
        public bool Sinal10 = true;
        public bool Sinal10Mudou = false;
        public bool Sinal30Mudou = false;
        // "Sinal 4.0": estratégia FLIP institucional (forma+fluxo+fita+risco+R:R+macro60+ExR).
        public bool Sinal40 = false;
        public bool Sinal40Mudou = false;

        // ── CONFIGURAÇÃO SALVÁVEL DOS SINAIS ──
        // Qual painel de config está aberto: 0=nenhum, 30=Sinal3.0, 20=Sinal2.0, 10=Sinal1.0
        public int ConfigAberto = 0;
        // Painel de estatísticas de assertividade (botão SINAIS) aberto?
        public bool StatAberto = false;
        // Modo de agressividade: true = Conservador (valida no fechamento, ✕ cancelado)
        // false = Agressivo (sem validação, mostra todos os sinais).
        public bool ModoConservador = true;
        public bool ModoMudou = false;   // sinaliza troca de modo p/ reprocessar
        // PLUS DIVERGÊNCIA: renovação de extremo + divergência RSI + delta. Vale para
        // 1.0 e 2.0, nos modos conservador e agressivo. Ligado pela engrenagem.
        public bool PlusDivergencia = false;
        // Dashboard visível? (botão X fecha o painel sem remover os sinais do gráfico)
        public bool DashboardVisivel = true;
        // Dashboard minimizado? (botão − reduz a uma barra compacta)
        public bool DashboardMinimizado = false;
        // Últimos valores das propriedades aplicados (p/ detectar edição nas opções).
        public bool _lastIniciar20 = false, _lastCfg10Cons = true, _lastCfg20Cons = true, _lastMostrarDash = true;
        public bool _propsAplicadasUmaVez = false;
        // Página do modal (para paginar muitos parâmetros): 0,1,2...
        public int ConfigPagina = 0;

        // Sinal 3.0 (Bipolaridade S&D)
        public double Cfg30_Agressao = 20.0;     // % agressão mínima
        public double Cfg30_TolZona = 0.3;       // tolerância zona (× ATR)
        public bool Cfg30_Inversao = true;       // inversão de polaridade
        public int Cfg30_ScoreMin = 70;          // score mínimo
        public bool Cfg30_ExigirGatilho = true;  // exigir gatilho de timing
        public bool Cfg30_FiltrarContraTend = true; // filtrar contra-tendência

        // Sinal 2.0 (confluência avançada)
        public int Cfg20_ScoreMin = 70;          // score mínimo
        public bool Cfg20_ExigirZona = true;     // exige estar em zona
        public bool Cfg20_ExigirDelta = true;    // exige delta na direção
        public int Cfg20_MinDivergencias = 1;    // divergências mínimas
        public bool Cfg20_UsarExaustao = true;   // usar exaustão de fluxo
        public bool Cfg20_PriorizarSR = true;    // priorizar suporte/resistência

        // Sinal 1.0 (modo simples)
        public int Cfg10_ScoreMin = 60;          // score mínimo (mais permissivo)
        public bool Cfg10_ApenasTendencia = true; // só opera a favor da tendência
        public int Cfg10_EmaPeriodo = 20;        // período da EMA de tendência
        public int Cfg10_SwingStrength = 3;      // força do swing

        // ── ESTRATÉGIA PADRÃO: pesos dos 3 pilares (configuráveis no painel) ──
        // Estrutura (pivô/zona/extremo) + Estocástico (K nos extremos 80/20) + Fluxo/Agressão.
        public int Peso_Estrutura = 40;   // price action + zigzag + S&D nos extremos
        public int Peso_Estocastico = 25; // K na extremidade 80/20
        public int Peso_Fluxo = 35;       // fluxo / saldo de agressão / times&trades
        public int Padrao_ScoreMin = 60;  // score mínimo para emitir
        public bool Estoc_585 = false;    // false = 3/5/3, true = 5/8/5

        // ── SINAL 2.0 (tendência ancorada em EMA) — parâmetros configuráveis no painel ──
        public bool Sinal20_UsarFlip = true;            // gatilho: flip de fluxo
        public bool Sinal20_UsarCandle = true;          // gatilho: candle de rejeição
        public bool Sinal20_UsarCruzamentoKD = true;    // gatilho: cruzamento K×D do estocástico
        public int Sinal20_AncoragemTol = 4;            // tolerância de toque na EMA (ticks)
        public int Sinal20_AncoragemBarras = 5;         // janela do pullback (barras)
        // núcleo de estrutura de tendência (EMA 9/30)
        public int Sinal20_ScoreMin = 65;               // score mínimo de estrutura p/ sinal (bolinha ≥65)
        public int Sinal20_SlopeMin = 5;                // slope mínimo das EMAs (×0.1 tick/5 barras)
        public int Sinal20_CompressaoMin = 6;           // distância mínima 9↔30 (ticks) antes de bloquear

        // Parâmetros GERAIS (compartilhados)
        public bool CfgGeral_AutoRegular = true;   // auto-regulação por timeframe
        public bool CfgGeral_Alertas = false;      // alertas sonoros
        public bool CfgGeral_Animacoes = true;     // animações leves

        // ── CAMADA 1: CONTEXTO E QUALIDADE DE MERCADO ──
        public bool   Ctx_Ativo = true;              // liga o filtro de contexto
        public double Ctx_AdxMinimo = 20.0;          // ADX mínimo
        public bool   Ctx_ExigirAdxCrescente = false;// exige ADX subindo
        public double Ctx_DistMaxEmaAtr = 1.0;       // distância máx preço→EMA (×ATR)
        public double Ctx_VolumeRelativoMin = 1.2;   // volume mínimo (×média)
        public int    Ctx_QualidadeMinima = 3;       // estrelas mínimas p/ operar

        // ── EMA de tendência escolhida para os motores 2.0 e 3.0 (9, 18 ou 30) ──
        public int Cfg20_EmaTendencia = 18;
        public int Cfg30_EmaTendencia = 9;

        // ── ETAPA 3: gate FLIP + FLUXO + R:R (do SinalConfluencia) ──
        public bool   Flip_Ativo = false;          // liga o gate flip (exige forma+fluxo)
        public int    Flip_DeltaMin = 50;          // quanto o delta precisa afundar antes de virar
        public bool   Flip_ExigirFita = true;      // exige fita viva (velocidade de negócios)
        public bool   Flip_ExigirRR = true;        // exige R:R mínimo até a próxima zona
        public double Flip_MinRR = 1.5;            // R:R mínimo
        public double Flip_MaxStopPts = 15.0;      // stop máximo em pontos

        // ── ETAPA 4: gates de contexto maior ──
        public bool Macro60_Exigir = false;        // bloqueia trade contra a tendência de 60m
        public bool ExR_Exigir = false;            // bloqueia entrada em absorção (esforço alto, resultado pequeno)
        public bool Tipo_Continuidade = true;      // aceita setups a favor da tendência
        public bool Tipo_Reversao = true;          // aceita setups contra a tendência
        public bool Tipo_Armadilha = true;         // aceita setups em zona de varredura

        // ── PERSISTÊNCIA: serializa/desserializa o estado como texto (chave=valor;) ──
        // Usado para salvar a configuração no workspace do NinjaTrader e restaurá-la
        // ao reabrir. Só os campos que o usuário configura são incluídos.
        public string Serializar()
        {
            var sb = new System.Text.StringBuilder();
            System.Action<string,object> add = (k,v) => sb.Append(k).Append('=').Append(
                System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            add("ModoFull", ModoFull); add("Sinal20", Sinal20); add("Sinal10", Sinal10);
            add("ModoConservador", ModoConservador); add("PlusDivergencia", PlusDivergencia);
            add("DashboardVisivel", DashboardVisivel);
            add("Cfg30_Agressao", Cfg30_Agressao); add("Cfg30_TolZona", Cfg30_TolZona);
            add("Cfg30_Inversao", Cfg30_Inversao); add("Cfg30_ScoreMin", Cfg30_ScoreMin);
            add("Cfg30_ExigirGatilho", Cfg30_ExigirGatilho); add("Cfg30_FiltrarContraTend", Cfg30_FiltrarContraTend);
            add("Peso_Estrutura", Peso_Estrutura); add("Peso_Estocastico", Peso_Estocastico);
            add("Peso_Fluxo", Peso_Fluxo); add("Padrao_ScoreMin", Padrao_ScoreMin); add("Estoc_585", Estoc_585);
            add("Sinal20_UsarFlip", Sinal20_UsarFlip); add("Sinal20_UsarCandle", Sinal20_UsarCandle);
            add("Sinal20_UsarCruzamentoKD", Sinal20_UsarCruzamentoKD);
            add("Sinal20_ScoreMin", Sinal20_ScoreMin); add("Sinal20_SlopeMin", Sinal20_SlopeMin);
            add("Sinal20_AncoragemTol", Sinal20_AncoragemTol); add("Sinal20_AncoragemBarras", Sinal20_AncoragemBarras);
            add("CfgGeral_AutoRegular", CfgGeral_AutoRegular); add("CfgGeral_Animacoes", CfgGeral_Animacoes);
            add("Cfg10_ScoreMin", Cfg10_ScoreMin); add("Cfg10_EmaPeriodo", Cfg10_EmaPeriodo);
            add("Cfg10_SwingStrength", Cfg10_SwingStrength); add("Cfg10_ApenasTendencia", Cfg10_ApenasTendencia);
            return sb.ToString();
        }

        public void Desserializar(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return;
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var par in txt.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(par)) continue;
                    int eq = par.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = par.Substring(0, eq), v = par.Substring(eq + 1);
                    switch (k)
                    {
                        case "ModoFull": ModoFull = bool.Parse(v); break;
                        case "Sinal20": Sinal20 = bool.Parse(v); break;
                        case "Sinal10": Sinal10 = bool.Parse(v); break;
                        case "ModoConservador": ModoConservador = bool.Parse(v); break;
                        case "PlusDivergencia": PlusDivergencia = bool.Parse(v); break;
                        case "DashboardVisivel": DashboardVisivel = bool.Parse(v); break;
                        case "Cfg30_Agressao": Cfg30_Agressao = double.Parse(v, ci); break;
                        case "Cfg30_TolZona": Cfg30_TolZona = double.Parse(v, ci); break;
                        case "Cfg30_Inversao": Cfg30_Inversao = bool.Parse(v); break;
                        case "Cfg30_ScoreMin": Cfg30_ScoreMin = int.Parse(v, ci); break;
                        case "Cfg30_ExigirGatilho": Cfg30_ExigirGatilho = bool.Parse(v); break;
                        case "Cfg30_FiltrarContraTend": Cfg30_FiltrarContraTend = bool.Parse(v); break;
                        case "Peso_Estrutura": Peso_Estrutura = int.Parse(v, ci); break;
                        case "Peso_Estocastico": Peso_Estocastico = int.Parse(v, ci); break;
                        case "Peso_Fluxo": Peso_Fluxo = int.Parse(v, ci); break;
                        case "Padrao_ScoreMin": Padrao_ScoreMin = int.Parse(v, ci); break;
                        case "Estoc_585": Estoc_585 = bool.Parse(v); break;
                        case "Sinal20_UsarFlip": Sinal20_UsarFlip = bool.Parse(v); break;
                        case "Sinal20_UsarCandle": Sinal20_UsarCandle = bool.Parse(v); break;
                        case "Sinal20_UsarCruzamentoKD": Sinal20_UsarCruzamentoKD = bool.Parse(v); break;
                        case "Sinal20_ScoreMin": Sinal20_ScoreMin = int.Parse(v, ci); break;
                        case "Sinal20_SlopeMin": Sinal20_SlopeMin = int.Parse(v, ci); break;
                        case "Sinal20_AncoragemTol": Sinal20_AncoragemTol = int.Parse(v, ci); break;
                        case "Sinal20_AncoragemBarras": Sinal20_AncoragemBarras = int.Parse(v, ci); break;
                        case "CfgGeral_AutoRegular": CfgGeral_AutoRegular = bool.Parse(v); break;
                        case "CfgGeral_Animacoes": CfgGeral_Animacoes = bool.Parse(v); break;
                        case "Cfg10_ScoreMin": Cfg10_ScoreMin = int.Parse(v, ci); break;
                        case "Cfg10_EmaPeriodo": Cfg10_EmaPeriodo = int.Parse(v, ci); break;
                        case "Cfg10_SwingStrength": Cfg10_SwingStrength = int.Parse(v, ci); break;
                        case "Cfg10_ApenasTendencia": Cfg10_ApenasTendencia = bool.Parse(v); break;
                    }
                }
            }
            catch { }
        }
    }

    public class SINAIS : Indicator
    {
        #region Classes Internas da Lógica Core
        public class Zone
        {
            public double h = 0.0;
            public double l = 0.0;
            public int b = 0;
            public int e = 0;
            public string t = "";
            public string c = "";
            public bool a = true;
            public bool rompida = false;   // bipolaridade: já foi rompida (polaridade invertida)
            public double poc = 0.0;       // Point of Control (preço de maior volume) da região
            public double vah = 0.0;       // Value Area High (borda superior da zona de valor)
            public double val = 0.0;       // Value Area Low (borda inferior da zona de valor)
            
            public Zone(double l, double h, int b, string t, string c, bool a)
            {
                this.l = l; this.h = h; this.b = b; this.t = t; this.c = c; this.a = a;
            }

            // Tipo EFETIVO: se rompida, inverte (supply↔demand).
            public string TipoEfetivo { get { return rompida ? (t == "s" ? "d" : "s") : t; } }
        }

        public class PivotSnapshot
        {
            public int BarIndex { get; set; }
            public double PriceHigh { get; set; }
            public double PriceLow { get; set; }
            public double PriceClose { get; set; }
            public double Delta { get; set; }
            public double Volume { get; set; }
            public double Rsi { get; set; }
            public double Bop { get; set; }
            public double Stoch { get; set; }
            public bool IsHigh { get; set; }
            public double ExactTriggerPrice { get; set; } 
        }
        #endregion

        #region Parâmetros e Variáveis

        [NinjaScriptProperty, Display(Name = "Chave de Licença", Order = 0, GroupName = "0 - Licença")]
        public string LicenseKey { get; set; }

        [NinjaScriptProperty, Display(Name = "Ajuste Automático", Order = 0, GroupName = "1. Estrutura e Pivôs")]
        public bool AutoRegular { get; set; }

        [NinjaScriptProperty, Display(Name = "Animações Leves", Order = 2, GroupName = "1. Estrutura e Pivôs")]
        public bool AnimacoesLeves { get; set; }

        [NinjaScriptProperty, Display(Name = "Método de Confirmação", Order = 1, GroupName = "1. Estrutura e Pivôs")]
        public ModoConfirmacaoTopoFundo ModoConfirmacao { get; set; }

        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Força do Pivô (Swing Strength)", Order = 3, GroupName = "1. Estrutura e Pivôs")]
        public int SwingStrength { get; set; }
        
        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Mín. Barras Entre Pivôs", Order = 4, GroupName = "1. Estrutura e Pivôs")]
        public int MinBarsBetweenSwings { get; set; }
        
        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Máx. Barras Entre Pivôs", Order = 5, GroupName = "1. Estrutura e Pivôs")]
        public int MaxBarsBetweenSwings { get; set; }
        
        [NinjaScriptProperty, Range(0, int.MaxValue), Display(Name = "Diferença Mínima Preço (Ticks)", Order = 6, GroupName = "1. Estrutura e Pivôs")]
        public int MinPriceDifferenceTicks { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Exigir Supply & Demand", Order = 0, GroupName = "2. Confluência e Regras")]
        public bool FiltrarPorSupplyDemand { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 5), Display(Name = "Mín. Divergências Necessárias", Order = 1, GroupName = "2. Confluência e Regras")]
        public int MinDivergencesRequired { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Usar Divergência Delta", Order = 2, GroupName = "2. Confluência e Regras")]
        public bool UseDeltaDivergence { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Usar Divergência Volume", Order = 3, GroupName = "2. Confluência e Regras")]
        public bool UseVolumeDivergence { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Usar Divergência RSI", Order = 4, GroupName = "2. Confluência e Regras")]
        public bool UseRsiDivergence { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Usar Divergência BOP", Order = 5, GroupName = "2. Confluência e Regras")]
        public bool UseBopDivergence { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Usar Divergência Estocástico", Order = 6, GroupName = "2. Confluência e Regras")]
        public bool UseStochDivergence { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Tolerância Delta", Order = 1, GroupName = "3. Parâmetros dos Indicadores")]
        public double DeltaTolerance { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Período RSI", Order = 2, GroupName = "3. Parâmetros dos Indicadores")]
        public int RsiPeriod { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Tolerância RSI", Order = 3, GroupName = "3. Parâmetros dos Indicadores")]
        public double RsiTolerance { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Período Suavização BOP", Order = 4, GroupName = "3. Parâmetros dos Indicadores")]
        public int BopSmoothingPeriod { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Tolerância BOP", Order = 5, GroupName = "3. Parâmetros dos Indicadores")]
        public double BopTolerance { get; set; }
        
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Tolerância Volume", Order = 6, GroupName = "3. Parâmetros dos Indicadores")]
        public double VolumeTolerance { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Estocástico Período K", Order = 7, GroupName = "3. Parâmetros dos Indicadores")]
        public int StochPeriodK { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Estocástico Período D", Order = 8, GroupName = "3. Parâmetros dos Indicadores")]
        public int StochPeriodD { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, int.MaxValue), Display(Name = "Estocástico Suavização", Order = 9, GroupName = "3. Parâmetros dos Indicadores")]
        public int StochSmooth { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Tolerância Estocástico", Order = 10, GroupName = "3. Parâmetros dos Indicadores")]
        public double StochTolerance { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Formato do Sinal de Contexto", Order = 0, GroupName = "4. Visual e Sinais")]
        public TipoSinal FormatoDoSinal { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        [Display(Name = "Cor Completo — Venda", Order = 11, GroupName = "0. Dashboard e Estratégia")]
        public System.Windows.Media.Brush TopDivergenceBrush { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        [Display(Name = "Cor Completo — Compra", Order = 12, GroupName = "0. Dashboard e Estratégia")]
        public System.Windows.Media.Brush BottomDivergenceBrush { get; set; }
        
        [NinjaScriptProperty, Display(Name = "Marcador Intracandle (em formação)", Order = 9, GroupName = "0. Dashboard e Estratégia")]
        public bool MostrarMarcadorIntracandle { get; set; }

        [NinjaScriptProperty, Display(Name = "▸ DASHBOARD  |  Exibir Dashboard", Order = 0, GroupName = "0. Dashboard e Estratégia")]
        public bool MostrarDashboard { get; set; }

        // Estratégia inicial: escolhe 1.0 ou 2.0 e se começa em modo conservador.
        [NinjaScriptProperty, Display(Name = "▸ ESTRATÉGIA  |  1 — Zones", Order = 1, GroupName = "0. Dashboard e Estratégia")]
        public bool Iniciar_Sinal10 { get; set; }

        [NinjaScriptProperty, Display(Name = "Estratégia 2 — EMA", Order = 2, GroupName = "0. Dashboard e Estratégia")]
        public bool Iniciar_Sinal20 { get; set; }

        [NinjaScriptProperty, Display(Name = "Perfil 1 — Conservador (✓) / Agressivo", Order = 3, GroupName = "0. Dashboard e Estratégia")]
        public bool Cfg10_Conservador { get; set; }

        [NinjaScriptProperty, Display(Name = "Perfil 2 — Conservador (✓) / Agressivo", Order = 4, GroupName = "0. Dashboard e Estratégia")]
        public bool Cfg20_Conservador { get; set; }

        [NinjaScriptProperty, Display(Name = "▸ TIMING  |  Momento — Tick (✓) / Fechamento", Order = 8, GroupName = "0. Dashboard e Estratégia")]
        public bool SinalNoTick { get; set; }

        [NinjaScriptProperty, Display(Name = "▸ SINAL COMPLETO  |  Formato", Order = 10, GroupName = "0. Dashboard e Estratégia")]
        public TipoSinal FormatoSinalCompleto { get; set; }

        [NinjaScriptProperty, Display(Name = "▸ SINAL PARCIAL  |  Formato", Order = 13, GroupName = "0. Dashboard e Estratégia")]
        public TipoSinal FormatoSinalParcial { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        [Display(Name = "Cor Parcial", Order = 15, GroupName = "0. Dashboard e Estratégia")]
        public System.Windows.Media.Brush CorSinalParcial { get; set; }

        [Browsable(false)]
        public string CorSinalParcialSerialize
        {
            get { return NinjaTrader.Gui.Serialize.BrushToString(CorSinalParcial); }
            set { CorSinalParcial = NinjaTrader.Gui.Serialize.StringToBrush(value); }
        }

        // ── SINAL CANCELADO (só no modo Conservador) ──
        // No perfil CONSERVADOR, um sinal que aparece mas se DESFAZ no fechamento do
        // candle (o setup não se confirmou na barra seguinte) é marcado como "cancelado".
        // Por padrão ele fica em cinza; aqui você pode escolher a cor.
        [System.Xml.Serialization.XmlIgnore]
        [Display(Name = "Cor Sinal Cancelado (Conservador — setup que se desfez no fechamento)", Order = 16, GroupName = "0. Dashboard e Estratégia")]
        public System.Windows.Media.Brush CorSinalCancelado { get; set; }

        [Browsable(false)]
        public string CorSinalCanceladoSerialize
        {
            get { return NinjaTrader.Gui.Serialize.BrushToString(CorSinalCancelado); }
            set { CorSinalCancelado = NinjaTrader.Gui.Serialize.StringToBrush(value); }
        }

        // Se ligado, o indicador SÓ calcula/emite sinais quando o preço está numa zona
        // de liquidez (supply/demand/bipolaridade). Fora das zonas, ignora tudo.
        [NinjaScriptProperty, Display(Name = "▸ ZONAS  |  Operar Apenas Dentro das Zonas", Order = 5, GroupName = "0. Dashboard e Estratégia")]
        public bool SomenteNasZonas { get; set; }

        // % da zona a partir do qual começa a analisar/dar sinal, medindo do lado de
        // ENTRADA para o EXTREMO de falha. 0 = zona inteira. 50 = só da metade ao extremo.
        // 100 = extremo (topo na venda / fundo na compra). Faixa 0-100.
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Intensidade da Zona (0=início · 100=extremo)", Order = 6, GroupName = "0. Dashboard e Estratégia")]
        public int InicioZonaPct { get; set; }

        // Timeframe (em minutos) de onde vêm as ZONAS de liquidez que servem de PILAR
        // para todo o cálculo dos sinais. 0 = usa o timeframe do próprio gráfico.
        [NinjaScriptProperty, Range(0, 240), Display(Name = "Timeframe das Zonas (min · 0=gráfico atual)", Order = 7, GroupName = "0. Dashboard e Estratégia")]
        public int TimeframeZonaMin { get; set; }

        // FILTRO DE TENDÊNCIA DO 15 MIN — quando ligado, sinais CONTRA a tendência do
        // 15min não são pintados como completos (100%); aparecem apenas como parciais.
        // O sinal continua aparecendo — só não recebe o status de completo sem o respaldo
        // da tendência maior. A favor da tendência, o sinal pode ser completo normalmente.
        [NinjaScriptProperty, Display(Name = "▸ TENDÊNCIA  |  Só 100% a favor do 15min", Order = 17, GroupName = "0. Dashboard e Estratégia")]
        public bool FiltrarTendencia15min { get; set; }

        // REFORÇO DE DELTA — quando ligado, conta uma confluência extra quando há
        // divergência de delta (preço renova extremo mas a agressão não acompanha) ou
        // exaustão de delta (pico de agressão seguido de colapso). No NASDAQ, esses são
        // dos sinais de reversão mais confiáveis.
        [NinjaScriptProperty, Display(Name = "▸ FLUXO  |  Reforço de Delta (divergência/exaustão)", Order = 18, GroupName = "0. Dashboard e Estratégia")]
        public bool ReforcoDelta { get; set; }

        // USAR AS DUAS ESTRATÉGIAS JUNTAS — quando ligado, o indicador avalia a 1.0
        // (regiões) E a 2.0 (EMA) ao mesmo tempo. Numa mesma barra, se as duas gerarem
        // sinal, a 2.0 (tendência) tem prioridade; se a 2.0 não gerar, usa a 1.0.
        [NinjaScriptProperty, Display(Name = "▸ ESTRATÉGIA  |  Usar 1.0 e 2.0 ao mesmo tempo", Order = 19, GroupName = "0. Dashboard e Estratégia")]
        public bool UsarAmbasEstrategias { get; set; }

        // CONFLUÊNCIA DE POC (Perfil de Volume) — quando ligado, sinais perto do Point of
        // Control (faixa de maior volume) de um pivô ganham uma confluência extra.
        // Funciona em qualquer gráfico: volumétrico usa volume real por preço; gráfico
        // normal (minuto/MTF) aproxima o perfil de volume pela faixa das barras.
        [NinjaScriptProperty, Display(Name = "▸ VOLUME  |  Confluência de POC dos Pivôs", Order = 20, GroupName = "0. Dashboard e Estratégia")]
        public bool UsarConfluenciaPOC { get; set; }

        [NinjaScriptProperty, Range(1, 100), Display(Name = "Tolerância do POC (ticks)", Order = 21, GroupName = "0. Dashboard e Estratégia")]
        public int PocToleranciaTicks { get; set; }

        // FILTRO DE VOLUME PROFILE (range de consolidação) — quando ligado, em regiões de
        // consolidação o indicador só deixa passar sinais nas BORDAS da Value Area
        // (VAH/VAL) ou no POC. No meio da zona de valor (ruído lateral), corta o sinal.
        [NinjaScriptProperty, Display(Name = "▸ VOLUME  |  Filtro Volume Profile (bordas VA/POC)", Order = 22, GroupName = "0. Dashboard e Estratégia")]
        public bool UsarFiltroVolumeProfile { get; set; }

        // Se ligado, o filtro considera também o Volume Profile (POC/VAH/VAL) das ZONAS
        // ativas (pivôs, suporte/resistência, supply & demand), não só da consolidação.
        [NinjaScriptProperty, Display(Name = "VP também nas Zonas (pivôs / S&R / S&D)", Order = 23, GroupName = "0. Dashboard e Estratégia")]
        public bool VpNasZonas { get; set; }

        // Desenha as linhas do Volume Profile no gráfico: POC em vermelho, VAH e VAL em amarelo.
        [NinjaScriptProperty, Display(Name = "Desenhar Linhas POC/VAH/VAL", Order = 27, GroupName = "0. Dashboard e Estratégia")]
        public bool DesenharVolumeProfile { get; set; }

        // Mini dashboard estatístico no canto do gráfico: conta os sinais por estratégia
        // e lista os últimos com os parâmetros/confluências que foram acatados.
        [NinjaScriptProperty, Display(Name = "▸ PAINEL  |  Mini Dashboard Estatístico", Order = 28, GroupName = "0. Dashboard e Estratégia")]
        public bool MostrarPainelEstatistico { get; set; }

        // Pontos de reversão que caracterizam LOSS: se, na barra seguinte ao sinal, o preço
        // volta este tanto de pontos contra a direção do sinal, ele é contado como loss.
        [NinjaScriptProperty, Range(1, 500), Display(Name = "Pontos p/ LOSS (reversão barra seguinte)", Order = 29, GroupName = "0. Dashboard e Estratégia")]
        public double PontosLossReverso { get; set; }

        // Marca d'água discreta no canto inferior direito mostrando a estratégia ativa (1.0/2.0).
        [NinjaScriptProperty, Display(Name = "▸ MARCA  |  Mostrar Estratégia no Gráfico", Order = 30, GroupName = "0. Dashboard e Estratégia")]
        public bool MostrarMarcaEstrategia { get; set; }

        [NinjaScriptProperty, Range(5, 200), Display(Name = "Janela da Consolidação (barras)", Order = 24, GroupName = "0. Dashboard e Estratégia")]
        public int VpJanelaBarras { get; set; }

        [NinjaScriptProperty, Range(1, 100), Display(Name = "Tolerância das Bordas VA/POC (ticks)", Order = 25, GroupName = "0. Dashboard e Estratégia")]
        public int VpToleranciaTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 10.0), Display(Name = "Sensibilidade da Consolidação (× ATR)", Order = 26, GroupName = "0. Dashboard e Estratégia")]
        public double VpConsolidacaoFator { get; set; }

        // Mostra (marcado) ou oculta (desmarcado) os sinais PARCIAIS (cinza).
        [NinjaScriptProperty, Display(Name = "Exibir Sinais Parciais (SIM/NÃO)", Order = 14, GroupName = "0. Dashboard e Estratégia")]
        public bool MostrarSinaisParciais { get; set; }

        [NinjaScriptProperty, Display(Name = "Modo de Posição do Painel", Order = 2, GroupName = "5. Dashboard Premium")]
        public PosicaoDashboard PosicaoPainel { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Mostrar Zonas S&D", Order = 1, GroupName = "6. Supply & Demand")]
        public bool MostrarZonasSD { get; set; }

        [NinjaScriptProperty, Display(Name = "Usar Painel Flutuante (todas as telas)", Order = 2, GroupName = "7. Posição do Dashboard Livre")]
        public bool UsarPainelFlutuante { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Exigir Gatilho Delta Girando (Estágio 1)", Order = 5, GroupName = "9. Score e Confluências")]
        public bool ExigirGatilhoTiming { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Aceitar Gatilho na Zona (sem cruzamento)", Order = 7, GroupName = "9. Score e Confluências")]
        public bool AceitarGatilhoNaZona { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Priorizar Suporte/Resistência", Order = 10, GroupName = "9. Score e Confluências")]
        public bool PriorizarSR { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0.1, 10.0), Display(Name = "Tolerância 'Perto de S/R' (× ATR)", Order = 11, GroupName = "9. Score e Confluências")]
        public double PriorizarSRTolerancia { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Penalidade 'Fora de S/R'", Order = 12, GroupName = "9. Score e Confluências")]
        public int PenalidadeForaSR { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Usar Exaustão de Fluxo (delta+BOP invertendo)", Order = 8, GroupName = "9. Score e Confluências")]
        public bool UsarExaustaoFluxo { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Exigir BOP E Delta juntos na exaustão", Order = 9, GroupName = "9. Score e Confluências")]
        public bool ExigirBopEDelta { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Filtrar Sinais Contra a Tendência", Order = 1, GroupName = "11. Filtro de Tendência")]
        public bool FiltrarContraTendencia { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 50), Display(Name = "EMA Lookback p/ Inclinação", Order = 2, GroupName = "11. Filtro de Tendência")]
        public int EmaTendenciaLookback { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0, 100), Display(Name = "ADX Mínimo p/ Tendência Definida", Order = 3, GroupName = "11. Filtro de Tendência")]
        public double AdxTendenciaMinimo { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Margem de Score p/ Reversão Forte", Order = 4, GroupName = "11. Filtro de Tendência")]
        public int MargemContraTendencia { get; set; }

        [Browsable(false)]
        [System.Xml.Serialization.XmlIgnore] [Display(Name = "Cor Demand Zone", Order = 2, GroupName = "6. Supply & Demand")]
        public System.Windows.Media.Brush demandColor { get; set; }

        [Browsable(false)]
        [System.Xml.Serialization.XmlIgnore] [Display(Name = "Cor Supply Zone", Order = 3, GroupName = "6. Supply & Demand")]
        public System.Windows.Media.Brush supplyColor { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0.0f, 1.0f), Display(Name = "Opacidade Linha", Order = 4, GroupName = "6. Supply & Demand")]
        public float activeLineOpacity { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0.0f, 1.0f), Display(Name = "Opacidade Área", Order = 5, GroupName = "6. Supply & Demand")]
        public float activeAreaOpacity { get; set; }

        [NinjaScriptProperty, Display(Name = "Deslocamento Horizontal Inicial", Order = 1, GroupName = "7. Posição do Dashboard Livre")]
        public float PainelDeslocamentoX { get; set; }

        [NinjaScriptProperty, Display(Name = "Deslocamento Vertical Inicial", Order = 2, GroupName = "7. Posição do Dashboard Livre")]
        public float PainelDeslocamentoY { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 10), Display(Name = "Barras Consecutivas Delta", Order = 1, GroupName = "8. Timing de Entrada (Segundo Plot)")]
        public int BarrasConsecutivasDelta { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 100), Display(Name = "Nível Estocástico Sobrecompra", Order = 2, GroupName = "8. Timing de Entrada (Segundo Plot)")]
        public int StochOverboughtLevel { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 100), Display(Name = "Nível Estocástico Sobrevenda", Order = 3, GroupName = "8. Timing de Entrada (Segundo Plot)")]
        public int StochOversoldLevel { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 50), Display(Name = "Período Média Delta (Candles)", Order = 4, GroupName = "8. Timing de Entrada (Segundo Plot)")]
        public int DeltaMediaPeriodo { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0.0, 500.0), Display(Name = "Aceleração Exigida Delta (%)", Order = 5, GroupName = "8. Timing de Entrada (Segundo Plot)")]
        public double DeltaAceleracaoPercentual { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(2, 100), Display(Name = "Período ADX", Order = 1, GroupName = "9. Score e Confluências")]
        public int AdxPeriodo { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(2, 200), Display(Name = "Período EMA", Order = 2, GroupName = "9. Score e Confluências")]
        public int EmaPeriodo { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0, 200), Display(Name = "Score Mínimo p/ Sinal", Order = 3, GroupName = "9. Score e Confluências")]
        public int ScoreMinimoSinal { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Sinal 1.0 — Limiar de Confluência", Order = 4, GroupName = "9. Score e Confluências")]
        public int Sinal10Limiar { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 20), Display(Name = "Sinal 2.0 — Tolerância de Ancoragem (ticks)", Order = 8, GroupName = "9. Score e Confluências")]
        public int EmaAncoragemTol { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 20), Display(Name = "Sinal 2.0 — Janela de Ancoragem (barras)", Order = 9, GroupName = "9. Score e Confluências")]
        public int EmaAncoragemBarras { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(5, 50), Display(Name = "Extremo da Zona (% da largura)", Order = 5, GroupName = "9. Score e Confluências")]
        public int ExtremoZonaPct { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 100), Display(Name = "Pontos p/ Possível Parcial", Order = 6, GroupName = "9. Score e Confluências")]
        public double PontosParcial { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Mostrar Sinais Históricos (backtest visual)", Order = 7, GroupName = "9. Score e Confluências")]
        public bool MostrarSinaisHistoricos { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Filtrar Horários (00-04:30 e 17-18h)", Order = 13, GroupName = "9. Score e Confluências")]
        public bool FiltrarHorario { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Validar no Fechamento (marca ✕ cancelado)", Order = 16, GroupName = "9. Score e Confluências")]
        public bool ValidarFechamento { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 200), Display(Name = "Estatística — Ticks p/ Gain", Order = 14, GroupName = "9. Score e Confluências")]
        public int TicksAlvoStatProp { get => TicksAlvoStat; set => TicksAlvoStat = value; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 200), Display(Name = "Estatística — Ticks do Stop (além do extremo)", Order = 15, GroupName = "9. Score e Confluências")]
        public int TicksStopStatProp { get => TicksStopStat; set => TicksStopStat = value; }

        [Browsable(false)]
        [NinjaScriptProperty, Range(0, 50), Display(Name = "Tolerância da Zona (Ticks)", Order = 8, GroupName = "9. Score e Confluências")]
        public int ZonaToleranciaTicks { get; set; }

        // ── ARQUITETURA INSTITUCIONAL: Região → Fluxo → Flip → Confirmações ──
        [Browsable(false)]
        [NinjaScriptProperty, Range(1, 100), Display(Name = "Máximo de Candles na Zona", Order = 9, GroupName = "9. Score e Confluências")]
        public int MaxBarsInsideZone { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Exigir Fluxo Coerente com a Região", Order = 10, GroupName = "9. Score e Confluências")]
        public bool ExigirFluxoRegiao { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Exigir Gatilho de Flip", Order = 11, GroupName = "9. Score e Confluências")]
        public bool ExigirFlip { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Confirmação: Divergência BOP", Order = 20, GroupName = "9. Score e Confluências")]
        public bool ConfDivBOP { get; set; }
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Confirmação: Divergência RSI", Order = 21, GroupName = "9. Score e Confluências")]
        public bool ConfDivRSI { get; set; }
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Confirmação: Divergência Estocástico", Order = 22, GroupName = "9. Score e Confluências")]
        public bool ConfDivStoch { get; set; }
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Confirmação: Divergência Delta", Order = 23, GroupName = "9. Score e Confluências")]
        public bool ConfDivDelta { get; set; }
        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Confirmação: Divergência Volume", Order = 24, GroupName = "9. Score e Confluências")]
        public bool ConfDivVolume { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Confirmação: Confluência (indicadores a favor)", Order = 25, GroupName = "9. Score e Confluências")]
        public bool ConfConfluencia { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Usar Sistema de Score", Order = 0, GroupName = "9. Score e Confluências")]
        public bool UsarSistemaScore { get; set; }

        [Browsable(false)]
        [NinjaScriptProperty, Display(Name = "Mostrar Timing Delta Girando", Order = 4, GroupName = "9. Score e Confluências")]
        public bool MostrarTimingDeltaGirando { get; set; }


        [Browsable(false)]
        public string TopDivergenceBrushSerialize
        {
            get { return NinjaTrader.Gui.Serialize.BrushToString(TopDivergenceBrush); }
            set { TopDivergenceBrush = NinjaTrader.Gui.Serialize.StringToBrush(value); }
        }
        [Browsable(false)]
        public string BottomDivergenceBrushSerialize
        {
            get { return NinjaTrader.Gui.Serialize.BrushToString(BottomDivergenceBrush); }
            set { BottomDivergenceBrush = NinjaTrader.Gui.Serialize.StringToBrush(value); }
        }

        // Persistência da configuração do dashboard (1.0/2.0, modo, pesos, PLUS, etc.).
        // O NinjaTrader salva esta string no workspace e a restaura ao reabrir.
        [Browsable(false)]
        public string EstadoConfigSerialize
        {
            get { return _estado != null ? _estado.Serializar() : _estadoSalvo; }
            set { _estadoSalvo = value; if (_estado != null) _estado.Desserializar(value); }
        }
        private string _estadoSalvo = "";
        [Browsable(false)]
        public string demandColorSerializable
        {
            get { return NinjaTrader.Gui.Serialize.BrushToString(demandColor); }
            set { demandColor = NinjaTrader.Gui.Serialize.StringToBrush(value); }
        }
        [Browsable(false)]
        public string supplyColorSerializable
        {
            get { return NinjaTrader.Gui.Serialize.BrushToString(supplyColor); }
            set { supplyColor = NinjaTrader.Gui.Serialize.StringToBrush(value); }
        }

        private Series<double> deltaSeries;
        private Series<double> bopSeries;
        private Series<double> bopSmoothed;

        // ── DELTA REAL tick a tick (Etapa 1 da fusão) ──
        // Medido em OnMarketData classificando cada Last contra bid/ask.
        // Funciona em qualquer tipo de gráfico e captura os extremos intra-barra
        // (essenciais para o FLIP das próximas etapas).
        private double barDeltaReal, barMinDelta, barMaxDelta;   // delta da barra e extremos
        private double currentBid = double.NaN, currentAsk = double.NaN;
        private int    deltaBarIndex = -1;   // controla o reset por barra
        private bool   usarDeltaReal = true; // liga o delta tick a tick (fallback p/ sintético se indisponível)

        // ── SINAL 1.0 (recomeço): rastreador de agressão dentro da região de pivô ──
        // Quando o preço entra numa zona, guardamos o pico de agressão do lado que
        // "chegou forte". A divergência acontece quando esse lado ENFRAQUECE dentro
        // da zona e o lado oposto assume — o gatilho final do sinal.
        private bool   _naZona = false;          // preço está dentro de uma região agora?
        private bool   _zonaVenda = false;       // a região atual é de venda (supply)?
        private double _aggPicoComprador = 0;    // pico de delta positivo desde que entrou na zona
        private double _aggPicoVendedor = 0;     // pico de delta negativo (mais negativo) na zona
        private double _deltaEntradaZona = 0;    // delta acumulado no momento em que entrou
        private int    _barsNaZona = 0;          // há quantas barras está na zona

        // ── Máquina de estados do CARD SINAL (PRÉ-SINAL → COMPRA/VENDA → PARCIAL → AGUARDANDO / CUIDADO) ──
        private bool     _sinalAtivo = false;
        private bool     _sinalVenda = false;
        private double   _sinalPrecoEntrada = 0;
        private DateTime _sinalHora = DateTime.MinValue;
        private DateTime _parcialAte = DateTime.MinValue;
        private bool     _parcialJaMostrada = false;
        private string   _cardEstado = "AGUARDANDO";
        // pré-sinal (candidato próximo mas ainda não confirmado)
        private bool     _preSinalAtivo = false;
        private bool     _preSinalVenda = false;

        // ── ETAPA 3: FITA rápida (velocidade de negócios) para o gate FLIP ──
        // Buffer circular de timestamps dos últimos negócios; conta quantos caíram
        // dentro da janela (tapeWindowMs). Fita "viva" = flip com participação real.
        private const int TAPE_CAP = 4096;
        private long[] tapeTs = new long[TAPE_CAP];
        private int tapeHead, tapeTail, tapeCount;
        private int TapeWindowMs = 3000;   // janela da fita (ms)
        private int TapeMinPrints = 15;    // mínimo de negócios na janela p/ fita "viva"

        // ── ETAPA 4: macro 60m + ExR ──
        private EMA emaFast60, emaMid60, emaSlow60;   // tendência na série de 60m
        private bool _serie60Add = false;             // série 60m foi adicionada?
        private int Fast60 = 9, Mid60 = 17, Slow60 = 30;
        // ── Série de 2min: fonte única das zonas de supply/demand para os sinais ──
        private bool _serie2mAdd = false;             // série 2min foi adicionada?
        private int _serieZonaIdx = -1;               // índice da série do timeframe da zona (-1 = não usa)
        // Cor do sinal parcial capturada da propriedade (RGB), segura para o render.
        private byte _corParcialR = 128, _corParcialG = 128, _corParcialB = 128;
        private byte _corCancR = 105, _corCancG = 105, _corCancB = 105;   // cor do cancelado (RGB)
        // cores dos sinais COMPLETOS capturadas das propriedades (venda/compra):
        private byte _corVendaR = 239, _corVendaG = 68, _corVendaB = 68;   // TopDivergenceBrush
        private byte _corCompraR = 34, _corCompraG = 197, _corCompraB = 94; // BottomDivergenceBrush
        private int _zonasMTFCriadas = 0;             // quantas zonas do TF secundário já foram criadas
        // Zonas do timeframe escolhido (TimeframeZonaMin), carregadas via BarsRequest.
        private System.Collections.Generic.List<Zone> _zonasMTF = new System.Collections.Generic.List<Zone>();
        private bool _zonasMTFProntas = false;        // o BarsRequest já terminou?
        private NinjaTrader.Data.BarsRequest _barsReqZona = null;
        // ── FILTRO DE TENDÊNCIA DO 15 MIN ──
        // Viés calculado do gráfico de 15min via BarsRequest: 1 = alta, -1 = baixa, 0 = neutro.
        private int _tendencia15 = 0;
        private bool _tend15Pronta = false;
        private NinjaTrader.Data.BarsRequest _barsReqTend15 = null;
        private DateTime _ultimaAtualizTend15 = DateTime.MinValue;
        private int _idx2m = -1;                      // índice da série 2min em BarsArray
        private int ExrWindowBars = 20;               // janela do ExR (barras)
        private double ExrEffortMult = 1.3;           // multiplicador de esforço (volume)
        private double ExrSpreadMult = 1.0;           // multiplicador de resultado (spread)
        private double[] volBufExr, spreadBufExr;     // buffers circulares
        private int bufHeadExr, bufCountExr;
        private double sumVolExr, sumSpreadExr;
        private int _exrEstado = 0;                   // 0 neutro, 1 absorção, 2 resultado

        // ── ETAPA 2: integração com EstruturaDipcorp (zonas fractais 5m+1m) ──
        // Acessadas por reflection cacheada: se o indicador Dipcorp estiver compilado,
        // o SINAIS SOMA essas zonas às suas próprias; se não estiver, ignora sem quebrar.
        private bool _dipcorpOk = false;                 // Dipcorp disponível?
        private System.Reflection.MethodInfo _dipZonas;  // Zonas(ativo)
        private System.Reflection.MethodInfo _dipConta;  // ContaZonasEm(ativo, preco, tol, out melhor)
        private System.Reflection.MethodInfo _dipAcima;  // ProximaAcima(ativo, preco)
        private System.Reflection.MethodInfo _dipAbaixo; // ProximaAbaixo(ativo, preco)
        private System.Reflection.PropertyInfo _dipEfetivoTopo; // ZonaLiq.EfetivoTopo
        private System.Reflection.PropertyInfo _dipVarredura;   // ZonaLiq.Varredura
        private System.Reflection.PropertyInfo _dipFonte;       // ZonaLiq.Fonte (timeframe)
        private System.Reflection.PropertyInfo _dipLo;          // ZonaLiq.Lo
        private System.Reflection.PropertyInfo _dipHi;          // ZonaLiq.Hi
        private System.Reflection.PropertyInfo _dipRompida;     // ZonaLiq.Rompida
        private double DipcorpTolPts = 2.0;              // tolerância de região (pontos) para as zonas Dipcorp
        private bool FiltrarSomente2min = true;         // sinais só nas zonas do 2min
        private RSI rsiIndicator;
        private Stochastics stochIndicator;
        private Stochastics stoch353;      // estocástico 3/5/3 dedicado (Sinais 1.0/2.0/3.0)
        private Stochastics stoch585;      // estocástico 5/8/5 (alternativa configurável)
        private EMA ema9;                  // EMAs de tendência para os novos motores
        private EMA ema18;
        private EMA ema30;
        private PivotSnapshot lastHighPivot;
        private PivotSnapshot lastLowPivot;

        private int atrPeriod = 10;
        private int currHiBar = 0, currLoBar = 0, prevHiBar = 0, prevLoBar = 0;
        private double currHiVal = 0.0, currLoVal = 0.0, prevHiVal = 0.0, prevLoVal = 0.0;
        private double atr;
        private const int MaxStoredZones = 300; // Limite de zonas retidas em memória para evitar crescimento ilimitado em históricos longos
        private List<Zone> Zones = new List<Zone>();
        private ATR atrInd;
        private ADX adxInd;
        private EMA emaInd;
        private SMA atrMediaInd;      // baseline de volatilidade (Camada 1)
        private SMA volumeMediaInd;   // baseline de liquidez (Camada 1)
        private KeltnerChannel kcInd;

        private DashboardEngine engine;
        // Estado de configuração POR GRÁFICO. Guardado num dicionário estático keyed
        // pelo ChartControl para: (a) ser isolado por janela (multi-instância) e
        // (b) SOBREVIVER a ReloadAllHistoricalData (que recria a instância do indicador).
        private static readonly System.Collections.Generic.Dictionary<object, DashboardEstado> _estadosPorGrafico
            = new System.Collections.Generic.Dictionary<object, DashboardEstado>();
        private DashboardEstado _estado;

        // Obtém (ou cria) o estado desta janela de gráfico. Preserva a config no reload.
        private DashboardEstado ObterEstado(out bool estadoNovo)
        {
            estadoNovo = false;
            object chave = ChartControl != null ? (object)ChartControl : this;
            lock (_estadosPorGrafico)
            {
                DashboardEstado e;
                if (!_estadosPorGrafico.TryGetValue(chave, out e) || e == null)
                {
                    e = new DashboardEstado();
                    // restaura a config salva no workspace (se houver) na primeira criação
                    if (!string.IsNullOrEmpty(_estadoSalvo)) e.Desserializar(_estadoSalvo);
                    _estadosPorGrafico[chave] = e;
                    estadoNovo = true;
                }
                return e;
            }
        }

        // ── Fingerprint estável do dispositivo (cacheado em disco, calculado uma única vez) ──
        private string GetDeviceFingerprint()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NinjaTrader 8", "ProfitAcademyLicense");
                string cachePath = Path.Combine(dir, "device_id.txt");

                if (File.Exists(cachePath))
                {
                    string cached = File.ReadAllText(cachePath).Trim();
                    if (!string.IsNullOrEmpty(cached)) return cached;
                }

                string raw = Environment.MachineName + "|" + Environment.UserName + "|"
                           + Environment.ProcessorCount + "|" + Environment.OSVersion.VersionString;

                string fingerprint;
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    var sb = new StringBuilder();
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    fingerprint = sb.ToString();
                }

                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(cachePath, fingerprint);
                }
                catch { /* se não conseguir salvar, recalcula na próxima vez — sem problema */ }

                return fingerprint;
            }
            catch
            {
                // fallback improvável: garante que sempre existe um valor de 32+ hex chars
                return SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName ?? "unknown"))
                    .Aggregate(new StringBuilder(), (sb, b) => sb.Append(b.ToString("x2"))).ToString();
            }
        }

        // ── Validação da licença junto ao backend Profit Academy ──
        // Chamada UMA vez por carregamento do gráfico (State.DataLoaded). Fail-open em
        // erro de rede/timeout (não bloqueia o usuário por instabilidade de internet);
        // fail-closed (bloqueia) apenas quando o servidor responde 403 "denied".
        private void ValidarLicenca()
        {
            string chave = (LicenseKey ?? "").Trim();
            if (string.IsNullOrEmpty(chave))
            {
                _licencaValida = false;
                _licencaMensagem = "Insira sua chave de licença nas propriedades do indicador (grupo \"0. Licença\").";
                return;
            }

            string fingerprint = GetDeviceFingerprint();
            string json = "{\"license_key\":\"" + chave.Replace("\"", "") + "\",\"device_fingerprint\":\"" + fingerprint + "\"}";

            try
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var respTask = _licencaHttpClient.PostAsync(LicenseValidateUrl, content);
                if (!respTask.Wait(TimeSpan.FromSeconds(15)))
                {
                    // timeout → fail-open
                    _licencaValida = true;
                    _licencaMensagem = "";
                    return;
                }
                var resp = respTask.Result;
                string body = resp.Content.ReadAsStringAsync().Result;

                if (resp.IsSuccessStatusCode)
                {
                    _licencaValida = true;
                    _licencaMensagem = "";
                    return;
                }

                if ((int)resp.StatusCode == 429)
                {
                    // rate limited → fail-open (tenta de novo na próxima abertura)
                    _licencaValida = true;
                    _licencaMensagem = "";
                    return;
                }

                // 403 (ou outro erro do domínio) → bloqueia, com mensagem por motivo
                string reason = ExtrairCampoJson(body, "reason");
                _licencaValida = false;
                switch (reason)
                {
                    case "expired":
                        _licencaMensagem = "Licença expirada — renove sua assinatura da Sala ao Vivo.";
                        break;
                    case "device_limit":
                        _licencaMensagem = "Limite de dispositivos atingido. Gerencie em seu Perfil no Profit Academy.";
                        break;
                    case "revoked":
                        _licencaMensagem = "Licença revogada. Contate o suporte.";
                        break;
                    case "not_found":
                        _licencaMensagem = "Chave de licença inválida.";
                        break;
                    default:
                        _licencaMensagem = "Licença não autorizada.";
                        break;
                }
            }
            catch
            {
                // falha de rede/DNS/etc → fail-open
                _licencaValida = true;
                _licencaMensagem = "";
            }
        }

        private static string ExtrairCampoJson(string json, string campo)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = Regex.Match(json, "\"" + campo + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private bool _uiInicializada = false;   // guard: evita registrar handlers/janela mais de uma vez

        // ── Licenciamento (Profit Academy) ──
        private const string LicenseValidateUrl = "https://kdprefcpspdpvtplihxj.supabase.co/functions/v1/validate-license";
        private static readonly HttpClient _licencaHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private bool _licencaChecada = false;   // já tentou validar nesta carga do gráfico?
        private bool _licencaValida = false;    // resultado da validação (granted = true)
        private string _licencaMensagem = "";   // mensagem exibida quando bloqueado
        private bool _reprocessando = false;    // guarda anti-reentrância do reprocessamento
        private int _ultimoBarSD = -1;          // última barra em que S&D/swings foram recalculados
        private int _barsRealtime = 0;          // barras processadas em tempo real (séries prontas)
        private bool _modoHistoricoAtivo = false; // durante backtest visual: pula reflection Dipcorp
        private int _sinaisHistPlotados = 0;    // teto de sinais no histórico (evita travar o render)
        private int _histCandidatos = 0;        // diagnóstico: candidatos detectados no histórico
        // Sinais registrados para render via SharpDX no OnRender (contorna o Draw.* que
        // não renderiza de forma confiável no histórico). Guarda barra, direção e nível.
        // resultado: 0 = pendente, 1 = gain (andou 10pts a favor), -1 = stop.
        private struct SinalMarca { public int bar; public bool venda; public bool seta; public bool hist; public double preco; public int resultado; public double entrada; public double stop; public double alvo; public bool cancelado; public bool parcial; public TipoSinal formato; public bool offset20; public double high; public double low; }
        private System.Collections.Generic.List<SinalMarca> _marcas = new System.Collections.Generic.List<SinalMarca>();
        private bool _marcasDiagFeito = false;
        private const int MAX_SINAIS_HIST = 5000; // limite de setas/bolinhas plotadas no backtest
        // ── Estatística de assertividade (painel SINAIS) ──
        private int _statGains = 0, _statStops = 0;

        // ── REGISTRO DE SINAIS (mini dashboard estatístico) ──
        // Cada sinal emitido é anotado com direção, estratégia, tipo e quais parâmetros/
        // confluências foram acatados. Alimenta o mini painel de estatísticas.
        private class RegistroSinal
        {
            public DateTime hora;
            public bool venda;
            public bool completo;
            public string estrategia;    // "1.0" ou "2.0"
            public string confluencias;  // parâmetros acatados
            public double preco;
            public double pavioAlto;     // máxima (pavio superior) do candle do sinal
            public double pavioBaixo;    // mínima (pavio inferior) do candle do sinal
            public int barSinal;         // CurrentBar em que o sinal saiu
            public int resultado;        // 0 = pendente, 1 = win, -1 = loss
        }
        private System.Collections.Generic.List<RegistroSinal> _registrosSinais = new System.Collections.Generic.List<RegistroSinal>();
        private int _regCompras = 0, _regVendas = 0, _regCompletos = 0, _regParciais = 0;
        private int _regWins = 0, _regLosses = 0;
        private bool _historicoAvaliado = false;   // já avaliou o win/loss do histórico?
        // posição do mini painel (arrastável). -1 = ainda não posicionado (usa canto padrão).
        private float _painelEstX = -1, _painelEstY = -1;
        private bool _arrastandoPainel = false;
        private float _arrasteDX = 0, _arrasteDY = 0;
        private SharpDX.RectangleF _painelEstRect = new SharpDX.RectangleF(0,0,0,0);

        // Avalia LOSS: se o candle SEGUINTE vier contra e passar PontosLossReverso pontos
        // ALÉM DO PAVIO do candle que deu o sinal, é loss; caso contrário, win.
        //  • Venda: loss se a máxima do candle seguinte > pavio ALTO do sinal + pontos.
        //  • Compra: loss se a mínima do candle seguinte < pavio BAIXO do sinal - pontos.
        private void AvaliarResultadosPendentes()
        {
            try
            {
                double passo = PontosLossReverso;   // pontos na escala do instrumento
                foreach (var r in _registrosSinais)
                {
                    if (r.resultado != 0) continue;
                    // avalia exatamente na barra seguinte ao sinal
                    if (CurrentBar != r.barSinal + 1) continue;
                    if (r.venda)
                    {
                        // venda: loss se o candle seguinte rompeu o pavio ALTO do sinal + pontos
                        if (High[0] >= r.pavioAlto + passo) { r.resultado = -1; _regLosses++; }
                        else { r.resultado = 1; _regWins++; }
                    }
                    else
                    {
                        // compra: loss se o candle seguinte rompeu o pavio BAIXO do sinal - pontos
                        if (Low[0] <= r.pavioBaixo - passo) { r.resultado = -1; _regLosses++; }
                        else { r.resultado = 1; _regWins++; }
                    }
                }
            }
            catch { }
        }

        private void RegistrarNoHistorico(bool venda, bool completo, string estrategia, string confluencias, double preco)
        {
            try
            {
                var r = new RegistroSinal { hora = Time[0], venda = venda, completo = completo, estrategia = estrategia, confluencias = confluencias ?? "", preco = preco, pavioAlto = High[0], pavioBaixo = Low[0], barSinal = CurrentBar, resultado = 0 };
                _registrosSinais.Add(r);
                if (_registrosSinais.Count > 300) _registrosSinais.RemoveAt(0);
                if (venda) _regVendas++; else _regCompras++;
                if (completo) _regCompletos++; else _regParciais++;
            }
            catch { }
        }

        // Avalia o win/loss de TODOS os sinais pendentes de uma vez (usado ao final do
        // reprocessamento histórico, quando todas as barras seguintes já existem).
        // Usa o pavio do candle do sinal + PontosLossReverso, avaliando a barra seguinte.
        private void AvaliarTodosHistorico()
        {
            try
            {
                double passo = PontosLossReverso;
                _regWins = 0; _regLosses = 0;
                foreach (var r in _registrosSinais)
                {
                    // barra seguinte ao sinal, em barsAgo relativo à barra atual
                    int barsAgoSeguinte = CurrentBar - (r.barSinal + 1);
                    if (barsAgoSeguinte < 0) { r.resultado = 0; continue; }   // ainda sem barra seguinte
                    int resultado;
                    if (r.venda)
                        resultado = (High[barsAgoSeguinte] >= r.pavioAlto + passo) ? -1 : 1;
                    else
                        resultado = (Low[barsAgoSeguinte] <= r.pavioBaixo - passo) ? -1 : 1;
                    r.resultado = resultado;
                    if (resultado == 1) _regWins++; else if (resultado == -1) _regLosses++;
                }
            }
            catch { }
        }
        private double _statPontosGanhos = 0, _statPontosPerdidos = 0;
        private int _statCompras = 0, _statVendas = 0;
        private int TicksAlvoStat = 35;   // ticks a favor para considerar gain
        private int TicksStopStat = 20;   // ticks além do extremo para o stop
        private string lastStatusHash = "";
        private System.Windows.Threading.DispatcherTimer animTimer;
        private PainelFlutuante painelFlutuante;

        private bool _isDragging = false;
        private System.Windows.Point _dragStartPoint;
        private float _panelStartDragX;
        private float _panelStartDragY;
        private float _mouseX = 0;
        private float _mouseY = 0;

        // ── ESTADO DO PRÉ-SINAL INTELIGENTE ──
        private string _preSinalEstado = "AGUARDANDO";
        private bool _preSinalIsVenda = false;
        private DateTime _preSinalInicio = DateTime.MinValue;

        // Buffer de preços para o mini-gráfico (últimos N fechamentos)
        private readonly System.Collections.Generic.List<double> _precoBuffer = new System.Collections.Generic.List<double>();
        private const int PRECO_BUFFER_MAX = 120;   // ~120 barras de histórico
        private int _ultimoBarPreco = -1;
        private DateTime _canceladoAte = DateTime.MinValue;
        private const double PRE_SINAL_SEGUNDOS = 10.0;    // tempo de confirmação
        private const double CANCELADO_SEGUNDOS = 2.0;     // tempo mostrando "CANCELADO"

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Engine de Sinais Premium + Dashboard UI/UX Institucional (Pro).";
                Name = "PROFIT ACADEMY PRO";
                LicenseKey = "";
                Calculate = Calculate.OnEachTick;   // permite modo tick; fechamento via IsFirstTickOfBar
                IsOverlay = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel = true;

                AutoRegular = true;               // detecta o timeframe e calibra os parâmetros sozinho
                AnimacoesLeves = false;           // por padrão animações completas; ligue se houver travamento
                ModoConfirmacao = ModoConfirmacaoTopoFundo.Pavio_E_Fechamento;
                SwingStrength = 5;
                MinBarsBetweenSwings = 10;
                MaxBarsBetweenSwings = 100;
                MinPriceDifferenceTicks = 2;
                
                FiltrarPorSupplyDemand = true;
                MinDivergencesRequired = 2;
                UseDeltaDivergence = true;
                UseVolumeDivergence = true;
                UseRsiDivergence = true;
                UseBopDivergence = true;
                UseStochDivergence = true;
                
                DeltaTolerance = 0;
                RsiPeriod = 14;
                RsiTolerance = 0.5;
                BopSmoothingPeriod = 1;
                BopTolerance = 0.05;
                VolumeTolerance = 0;
                StochPeriodD = 3;
                StochPeriodK = 14;
                StochSmooth = 3;
                StochTolerance = 0.5;
                
                FormatoDoSinal = TipoSinal.Seta;
                TopDivergenceBrush = System.Windows.Media.Brushes.Red;
                BottomDivergenceBrush = System.Windows.Media.Brushes.LimeGreen;
                MostrarMarcadorIntracandle = true;

                MostrarDashboard = true;
                Iniciar_Sinal10 = true;      // começa no 1.0
                Cfg10_Conservador = true;    // 1.0 conservador por padrão
                Iniciar_Sinal20 = false;
                Cfg20_Conservador = true;    // 2.0 conservador por padrão
                SinalNoTick = false;         // por padrão marca no FECHAMENTO (mais confiável)
                FormatoSinalParcial = TipoSinal.Bolinha;   // cinza parcial como bolinha
                FormatoSinalCompleto = TipoSinal.Seta;     // completo como seta
                CorSinalParcial = System.Windows.Media.Brushes.Gray;
                CorSinalCancelado = System.Windows.Media.Brushes.DimGray;   // cinza (setup desfeito)
                MostrarSinaisParciais = true;              // mostra os cinzas por padrão
                InicioZonaPct = 0;                         // 0 = analisa a zona inteira
                TimeframeZonaMin = 0;        // 0 = usa o timeframe do gráfico atual
                FiltrarTendencia15min = false;   // filtro de tendência do 15min desligado por padrão
                ReforcoDelta = true;             // reforço de delta ligado por padrão
                UsarAmbasEstrategias = false;    // por padrão, uma estratégia por vez
                UsarConfluenciaPOC = false;      // confluência de POC desligada por padrão
                PocToleranciaTicks = 8;          // tolerância de proximidade do POC
                UsarFiltroVolumeProfile = false; // filtro de volume profile desligado por padrão
                VpNasZonas = true;               // por padrão, considera o VP das zonas também
                DesenharVolumeProfile = false;   // desenho das linhas POC/VAH/VAL desligado por padrão
                MostrarPainelEstatistico = false; // mini dashboard estatístico desligado por padrão
                PontosLossReverso = 20;          // 20 pontos de reversão = loss
                MostrarMarcaEstrategia = true;   // marca d'água da estratégia ligada por padrão (nativo)
                VpJanelaBarras = 30;             // janela para detectar consolidação
                VpToleranciaTicks = 6;           // tolerância das bordas VA/POC
                VpConsolidacaoFator = 3.0;       // amplitude < 3× ATR = consolidação
                SomenteNasZonas = true;      // por padrão, só opera dentro das zonas
                PosicaoPainel = PosicaoDashboard.Livre_Arrastavel;

                MostrarZonasSD = false;   // zonas ocultas no gráfico (lógica continua usando)
                demandColor = System.Windows.Media.Brushes.YellowGreen; 
                supplyColor = System.Windows.Media.Brushes.Tomato;
                activeLineOpacity = 0.30f;
                activeAreaOpacity = 0.15f;
                
                PainelDeslocamentoX = 20f;
                PainelDeslocamentoY = 20f;

                BarrasConsecutivasDelta = 1;      // 1 candle (era 2) → menos atraso
                StochOverboughtLevel = 75;        // prompt: sobrecompra > 75
                StochOversoldLevel = 25;          // prompt: sobrevenda < 25
                DeltaMediaPeriodo = 2;            // comparar com os 2 candles anteriores (era 5)
                DeltaAceleracaoPercentual = 20.0; // prompt: ~20% acima da média (era 30)

                AdxPeriodo = 14;
                EmaPeriodo = 9;              // EMA 9 (curto prazo, como no prompt)
                ScoreMinimoSinal = 70;       // 70+ = bom setup
                Sinal10Limiar = 60;          // Sinal 1.0: 60/100 de confluência para emitir
                EmaAncoragemTol = 4;         // Sinal 2.0: tolerância (ticks) p/ considerar toque na EMA
                EmaAncoragemBarras = 5;      // Sinal 2.0: janela de barras p/ detectar o pullback
                ExtremoZonaPct = 15;         // sinal só nos últimos 15% da zona (extremo)
                PontosParcial = 12;          // pontos a favor para mostrar "POSSÍVEL PARCIAL" (10-15 típico)
                MostrarSinaisHistoricos = true; // backtest visual: plota sinais no histórico
                FiltrarHorario = true;          // bloqueia sinais 00-04:30 e 17-18h
                ValidarFechamento = true;       // marca ✕ cancelado se o sinal se desfaz no fechamento
                TicksAlvoStatProp = 35;         // 35 ticks a favor = gain
                TicksStopStatProp = 20;         // 20 ticks além do extremo = stop
                ZonaToleranciaTicks = 6;         // tolerância p/ considerar preço "na zona" (bipolaridade)

                // ── Arquitetura institucional (defaults) ──
                MaxBarsInsideZone = 10;          // após 10 candles na zona, região enfraquece
                ExigirFluxoRegiao = true;        // fluxo coerente com a região é obrigatório
                ExigirFlip = true;               // gatilho de flip obrigatório
                ConfDivBOP = false;
                ConfDivRSI = false;
                ConfDivStoch = false;
                ConfDivDelta = true;             // divergência de delta ligada por padrão
                ConfDivVolume = false;
                ConfConfluencia = true;          // confluência de indicadores ligada por padrão

                UsarSistemaScore = true;
                MostrarTimingDeltaGirando = true;

                UsarPainelFlutuante = true;         // janela WPF flutuante SEMPRE ativa (padrão fixo)
                ExigirGatilhoTiming = true;         // Estágio 1 (Delta Girando) ligado por padrão
                AceitarGatilhoNaZona = true;        // aceita sinal na zona mesmo sem cruzamento perfeito
                PriorizarSR = true;                 // reforça sinais em S/R, penaliza no meio do caminho
                PriorizarSRTolerancia = 1.5;        // "perto" = até 1.5× ATR da zona
                PenalidadeForaSR = 20;              // desconto no score p/ sinais longe de qualquer S/R
                UsarExaustaoFluxo = true;           // detecta exaustão de delta/BOP na zona (bipolaridade)
                ExigirBopEDelta = false;            // false = delta OU bop; true = exige os dois juntos (mais rígido)

                FiltrarContraTendencia = true;      // bloqueia compras em queda / vendas em alta
                EmaTendenciaLookback = 8;           // barras p/ medir inclinação da EMA
                AdxTendenciaMinimo = 20.0;          // ADX acima disso = tendência definida
                MargemContraTendencia = 35;         // reversão precisa de score = mínimo + 35 p/ furar o filtro
            }
            else if (State == State.Configure)
            {
                _serie60Add = false;
                _serie2mAdd = false;
                _serieZonaIdx = -1;   // zonas MTF agora vêm por BarsRequest, não por série
            }
            else if (State == State.DataLoaded)
            {
                // ── Validação de licença: uma vez por carregamento do gráfico ──
                if (!_licencaChecada)
                {
                    _licencaChecada = true;
                    ValidarLicenca();
                }
                if (!_licencaValida)
                {
                    // Não configura séries, dashboard, handlers etc. O indicador fica
                    // "vazio" até o próximo reload/reabertura do gráfico com licença válida.
                    return;
                }

                // estado persistente desta janela (sobrevive ao reload; isolado por gráfico)
                bool estadoNovo;
                _estado = ObterEstado(out estadoNovo);
                // Limpa objetos Draw nativos de sinais de versões antigas (agora o desenho
                // é 100% via SharpDX). Remove setas/dots/triângulos "presos" no gráfico.
                try
                {
                    foreach (var o in DrawObjects.ToList())
                    {
                        var dt = o as NinjaTrader.NinjaScript.DrawingTools.DrawingTool;
                        if (dt == null) continue;
                        string tg = dt.Tag ?? "";
                        if (tg.StartsWith("Sinal10_") || tg.StartsWith("Sinal20_") || tg.StartsWith("PreSinal_"))
                            RemoveDrawObject(tg);
                    }
                }
                catch { }
                // captura a cor do sinal parcial (thread segura) e congela o brush
                try
                {
                    var scb = CorSinalParcial as System.Windows.Media.SolidColorBrush;
                    if (scb != null)
                    {
                        if (scb.CanFreeze && !scb.IsFrozen) scb.Freeze();
                        _corParcialR = scb.Color.R; _corParcialG = scb.Color.G; _corParcialB = scb.Color.B;
                    }
                    var scbC = CorSinalCancelado as System.Windows.Media.SolidColorBrush;
                    if (scbC != null)
                    {
                        if (scbC.CanFreeze && !scbC.IsFrozen) scbC.Freeze();
                        _corCancR = scbC.Color.R; _corCancG = scbC.Color.G; _corCancB = scbC.Color.B;
                    }
                    var scbV = TopDivergenceBrush as System.Windows.Media.SolidColorBrush;
                    if (scbV != null)
                    {
                        if (scbV.CanFreeze && !scbV.IsFrozen) scbV.Freeze();
                        _corVendaR = scbV.Color.R; _corVendaG = scbV.Color.G; _corVendaB = scbV.Color.B;
                    }
                    var scbCp = BottomDivergenceBrush as System.Windows.Media.SolidColorBrush;
                    if (scbCp != null)
                    {
                        if (scbCp.CanFreeze && !scbCp.IsFrozen) scbCp.Freeze();
                        _corCompraR = scbCp.Color.R; _corCompraG = scbCp.Color.G; _corCompraB = scbCp.Color.B;
                    }
                }
                catch { }
                _zonasMTFCriadas = 0;   // reset do contador de zonas do TF secundário
                // Aplica a escolha das PROPRIEDADES quando: (a) o estado é novo, ou
                // (b) você ALTEROU uma propriedade desde a última carga. Assim a edição
                // nas opções é respeitada, mas os botões do dashboard também persistem
                // no F5 (quando as propriedades não mudaram).
                bool propsMudaram = !_estado._propsAplicadasUmaVez
                                 || (_estado._lastIniciar20 != Iniciar_Sinal20)
                                 || (_estado._lastCfg10Cons != Cfg10_Conservador)
                                 || (_estado._lastCfg20Cons != Cfg20_Conservador)
                                 || (_estado._lastMostrarDash != MostrarDashboard);
                if (estadoNovo || propsMudaram)
                {
                    if (Iniciar_Sinal20) { _estado.Sinal20 = true; _estado.Sinal10 = false; }
                    else                 { _estado.Sinal20 = false; _estado.Sinal10 = true; }
                    _estado.ModoConservador = _estado.Sinal20 ? Cfg20_Conservador : Cfg10_Conservador;
                    _estado.DashboardVisivel = MostrarDashboard;
                }
                // se "Mostrar Dashboard" está desmarcado, garante oculto sempre
                if (!MostrarDashboard) _estado.DashboardVisivel = false;
                // memoriza os valores aplicados para detectar mudança na próxima carga
                _estado._lastIniciar20 = Iniciar_Sinal20;
                _estado._lastCfg10Cons = Cfg10_Conservador;
                _estado._lastCfg20Cons = Cfg20_Conservador;
                _estado._lastMostrarDash = MostrarDashboard;
                _estado._propsAplicadasUmaVez = true;
                // Auto-regulação: detecta o timeframe do gráfico e ajusta os parâmetros
                // de S/D e timing para valores adequados (se AutoRegular estiver ligado).
                if (AutoRegular) AplicarPresetPorTimeframe();
                _estado.AnimacoesLeves = AnimacoesLeves;  // aplica o modo de animação escolhido

                deltaSeries = new Series<double>(this);
                bopSeries = new Series<double>(this);
                bopSmoothed = new Series<double>(this);
                
                rsiIndicator = RSI(Close, RsiPeriod, 1);
                stochIndicator = Stochastics(StochPeriodD, StochPeriodK, StochSmooth);
                // Estocásticos dedicados: 3/5/3 e 5/8/5 (trocável no painel via Estoc_585)
                stoch353 = Stochastics(3, 5, 3);
                stoch585 = Stochastics(5, 8, 5);
                ema9 = EMA(9);
                ema18 = EMA(18);
                ema30 = EMA(30);
                atrInd = ATR(atrPeriod);
                kcInd = KeltnerChannel(1.0, 10);
                adxInd = ADX(AdxPeriodo);      // filtro de contexto (força de tendência)
                emaInd = EMA(EmaPeriodo);      // filtro dinâmico de tendência de curto prazo
                atrMediaInd = SMA(atrInd, 20);        // média de ATR p/ regime de volatilidade

                volumeMediaInd = SMA(Volume, 20);     // média de volume p/ liquidez relativa

                // ── ETAPA 4: EMAs na série de 60m (se a série foi adicionada) + buffers ExR ──
                if (_serie60Add && BarsArray.Length > 1)
                {
                    emaFast60 = EMA(Closes[1], Fast60);
                    emaMid60  = EMA(Closes[1], Mid60);
                    emaSlow60 = EMA(Closes[1], Slow60);
                }
                // índice da série 2min (0=primária, 1=60m, 2=2min na ordem de AddDataSeries)
                _idx2m = _serie2mAdd ? 2 : -1;
                volBufExr = new double[Math.Max(1, ExrWindowBars)];
                spreadBufExr = new double[Math.Max(1, ExrWindowBars)];
                bufHeadExr = 0; bufCountExr = 0; sumVolExr = 0; sumSpreadExr = 0;

                lastHighPivot = null;
                lastLowPivot = null;
                Zones.Clear();

                // ── ETAPA 2: tenta localizar o EstruturaDipcorp (opcional) ──
                ResolverDipcorp();

                // O engine calcula as métricas usadas tanto pelo dashboard do gráfico
                // quanto pela janela flutuante — então é criado se qualquer um estiver ativo.
                if (MostrarDashboard || UsarPainelFlutuante)
                {
                    engine = new DashboardEngine(this, _estado);
                    engine.OnReprocessar = ReprocessarSinais;   // troca de modo/estratégia recalcula na hora
                }

                // ── ZONAS MULTI-TIMEFRAME via BarsRequest ──
                // Se o usuário escolheu um TF de zona, busca TODO o histórico desse TF de
                // uma vez e calcula as zonas antecipadamente. Assim as zonas já existem
                // quando qualquer sinal (passado ou presente) é avaliado.
                if (TimeframeZonaMin > 0)
                    DispararBarsRequestZonas();

                // Filtro de tendência do 15min: calcula o viés inicial via BarsRequest.
                if (FiltrarTendencia15min)
                    AtualizarTendencia15();
            }
            else if (State == State.Historical)
            {
                if (!_licencaValida) return;   // licença falhou em DataLoaded — não inicializa UI

                // GUARD: State.Historical pode ser chamado mais de uma vez durante o
                // carregamento. Sem isso, handlers de mouse se acumulam e múltiplas janelas
                // flutuantes são criadas → sobrecarga e crash no carregamento.
                if (_uiInicializada) return;
                _uiInicializada = true;

                if ((MostrarDashboard || MostrarPainelEstatistico) && ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        ChartControl.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
                        ChartControl.PreviewMouseMove += OnMouseMove;
                        ChartControl.PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;

                        // Timer de animação: redesenha o painel periodicamente para as
                        // animações rodarem mesmo com o gráfico parado. Taxa reduzida para
                        // aliviar CPU/GPU (principal causa de travamento do NinjaTrader).
                        animTimer = new System.Windows.Threading.DispatcherTimer();
                        animTimer.Interval = TimeSpan.FromMilliseconds(120);  // ~8fps: bem mais leve
                        animTimer.Tick += (s, e) =>
                        {
                            // Só invalida se o dashboard do gráfico está realmente visível.
                            try { if (MostrarDashboard && !UsarPainelFlutuante && ChartControl != null) ChartControl.InvalidateVisual(); } catch { }
                        };
                        animTimer.Start();
                    });
                }

                // Janela flutuante: independente do SO, livre entre todos os monitores.
                // Só cria se o dashboard estiver habilitado (MostrarDashboard) e não tiver
                // sido fechado pelo usuário (DashboardVisivel). Respeita o estado no F5.
                if (UsarPainelFlutuante && MostrarDashboard && _estado != null && _estado.DashboardVisivel)
                {
                    var disp = System.Windows.Application.Current != null
                        ? System.Windows.Application.Current.Dispatcher
                        : (ChartControl != null ? ChartControl.Dispatcher : null);
                    if (disp != null)
                    {
                        disp.InvokeAsync(() =>
                        {
                            try
                            {
                                if (painelFlutuante == null && engine != null)
                                {
                                    painelFlutuante = new PainelFlutuante(engine);
                                    painelFlutuante.Show();
                                }
                            }
                            catch { }
                        });
                    }
                }
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        ChartControl.PreviewMouseLeftButtonDown -= OnMouseLeftButtonDown;
                        ChartControl.PreviewMouseMove -= OnMouseMove;
                        ChartControl.PreviewMouseLeftButtonUp -= OnMouseLeftButtonUp;

                        try { if (animTimer != null) { animTimer.Stop(); animTimer = null; } } catch { }
                    });
                }

                // ORDEM SEGURA DE LIBERAÇÃO (evita crash ao trocar timeframe/recarregar):
                try { if (_barsReqZona != null) { _barsReqZona.Dispose(); _barsReqZona = null; } } catch { }
                try { if (_barsReqTend15 != null) { _barsReqTend15.Dispose(); _barsReqTend15 = null; } } catch { }
                // A janela flutuante usa o MESMO 'engine' para renderizar. Se destruirmos o
                // engine enquanto a janela ainda renderiza, há acesso a memória liberada → crash.
                // Por isso: primeiro paramos e fechamos a janela SÍNCRONAMENTE (Invoke, não
                // InvokeAsync), e só depois liberamos o engine.
                var janela = painelFlutuante;
                painelFlutuante = null;

                if (janela != null)
                {
                    var disp = System.Windows.Application.Current != null
                        ? System.Windows.Application.Current.Dispatcher
                        : (ChartControl != null ? ChartControl.Dispatcher : null);
                    if (disp != null)
                    {
                        try
                        {
                            // Invoke (síncrono) garante que a janela pare de renderizar e feche
                            // ANTES de seguirmos para o Dispose do engine.
                            disp.Invoke(() =>
                            {
                                try { janela.PararRender(); } catch { }
                                try { janela.Close(); } catch { }
                            });
                        }
                        catch { }
                    }
                }

                // Agora é seguro liberar o engine — ninguém mais o está usando.
                if (engine != null)
                {
                    try { engine.Dispose(); } catch { }
                    engine = null;
                }

                _uiInicializada = false;  // permite reinicializar a UI num próximo carregamento
            }
        }

        // Reprocessa os sinais no modo/estratégia atual SEM recarregar as séries de
        // dados (ReloadAllHistoricalData é pesadíssimo e afeta o NinjaTrader todo).
        // Aqui apenas zeramos as marcas/estatística e sinalizamos para reprocessar as
        // barras já em memória no próximo ciclo — leve e isolado a esta janela.
        private void ReprocessarSinais()
        {
            try
            {
                foreach (var t in tagsSinaisNormais) { try { RemoveDrawObject(t); } catch { } }
                foreach (var t in tagsSinais20)      { try { RemoveDrawObject(t); } catch { } }
                tagsSinaisNormais.Clear(); tagsSinais20.Clear();
            }
            catch { }
            _marcas.Clear();
            _statGains = _statStops = 0;
            _registrosSinais.Clear(); _regCompras = _regVendas = _regCompletos = _regParciais = 0; _regWins = _regLosses = 0; _historicoAvaliado = false;
            _statPontosGanhos = _statPontosPerdidos = 0;
            _statCompras = _statVendas = 0;
            _histCandidatos = 0; _sinaisHistPlotados = 0;
            _marcasDiagFeito = false;
            ultimoBarSinal = -1; _ultimoBarBolinha = -1; _ultimoBar20 = -1;
            // reprocessa as barras que já estão em memória (não recarrega séries)
            ReprocessarHistoricoLeve();
            // redesenha só esta janela
            try { if (ChartControl != null) ChartControl.InvalidateVisual(); } catch { }
        }

        // Recalcula os sinais do passado no modo/estratégia atual — EM MEMÓRIA, sem
        // ReloadAllHistoricalData (que no NinjaTrader equivale a um F5 e recarrega TODAS
        // as janelas/indicadores do workspace, sobrecarregando tudo).
        // Varre as barras já carregadas usando índices relativos (barsAgo) e reavalia
        // a estrutura de EMA/RSI, que são acessíveis por índice. Afeta só esta janela.
        // Verifica se o range [lo, hi] de uma barra toca alguma zona do TF escolhido,
        // do tipo coerente com a direção (venda→supply, compra→demand).
        private bool PrecoTocaZonaMTF(double hi, double lo, bool isVenda)
        {
            try
            {
                string tipo = isVenda ? "s" : "d";
                double tol = TickSize * ZonaToleranciaTicks;
                foreach (var z in _zonasMTF)
                {
                    if (z.TipoEfetivo != tipo) continue;
                    // sobreposição do range da barra com a zona (com tolerância)
                    if (hi >= z.l - tol && lo <= z.h + tol) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private void ReprocessarHistoricoLeve()
        {
            if (_reprocessando) return;
            _reprocessando = true;
            try
            {
                if (ema9 == null || ema30 == null || CurrentBar < 40) { _reprocessando = false; return; }
                int total = CurrentBar;
                int janela = Math.Min(total - 6, 1500);   // reavalia até ~1500 barras (performance)
                int ultimoBar = -100;

                for (int barsAgo = janela; barsAgo >= 1; barsAgo--)
                {
                    int barAbs = total - barsAgo;
                    bool isVenda;
                    int score = AvaliarEstruturaEMAEm(barsAgo, out isVenda);
                    // corte do passado por modo (mesmo critério do histórico)
                    double corte = _estado.Sinal20 ? 88.0 : 78.0;
                    // no 1.0 usamos a estrutura EMA como aproximação visual do passado
                    if (score >= corte && (barAbs - ultimoBar) >= 5)
                    {
                        double ent = Close[barsAgo];
                        // ── FILTRO DE ZONA (TF escolhido ou "somente nas zonas") ──
                        // Se um TF de zona foi definido, só aceita a marca se o preço
                        // daquela barra tocar uma zona do TF escolhido.
                        if (TimeframeZonaMin > 0 && _zonasMTFProntas && _zonasMTF.Count > 0)
                        {
                            double hi = High[barsAgo], lo = Low[barsAgo];
                            if (!PrecoTocaZonaMTF(hi, lo, isVenda)) continue;
                        }
                        else if (SomenteNasZonas)
                        {
                            double hi = High[barsAgo], lo = Low[barsAgo];
                            bool naZona = isVenda
                                ? (PertoDeSupply(hi) || PertoDeSupply(ent))
                                : (PertoDeDemand(lo) || PertoDeDemand(ent));
                            if (!naZona) continue;
                        }
                        double buf = TicksStopStat * TickSize;
                        double alvoDist = TicksAlvoStat * TickSize;
                        double stp, alv;
                        if (!isVenda)
                        {
                            double menor = Low[barsAgo];
                            for (int i = barsAgo + 1; i <= barsAgo + 3 && i <= total; i++) menor = Math.Min(menor, Low[i]);
                            stp = menor - buf; alv = ent + alvoDist;
                        }
                        else
                        {
                            double maior = High[barsAgo];
                            for (int i = barsAgo + 1; i <= barsAgo + 3 && i <= total; i++) maior = Math.Max(maior, High[i]);
                            stp = maior + buf; alv = ent - alvoDist;
                        }
                        // resolve gain/stop varrendo as barras seguintes (já em memória)
                        int res = ResolverGainStopEm(barsAgo, isVenda, stp, alv);
                        _marcas.Add(new SinalMarca { bar = barAbs, venda = isVenda, seta = true, hist = true, preco = ent, resultado = res, entrada = ent, stop = stp, alvo = alv, formato = FormatoSinalCompleto, high = High[barsAgo], low = Low[barsAgo] });
                        if (res == 1) { _statGains++; _statPontosGanhos += TicksAlvoStat; if (isVenda) _statVendas++; else _statCompras++; }
                        else if (res == -1) { _statStops++; _statPontosPerdidos += Math.Abs(ent - stp) / TickSize; if (isVenda) _statVendas++; else _statCompras++; }
                        ultimoBar = barAbs;
                    }
                }
            }
            catch { }
            _reprocessando = false;
            try { if (ChartControl != null) ChartControl.InvalidateVisual(); } catch { }
        }

        // Estrutura de EMA (9/30) avaliada por índice barsAgo — versão do motor 2.0 que
        // funciona sobre barras já carregadas (para o reprocessamento em memória).
        private int AvaliarEstruturaEMAEm(int barsAgo, out bool isVenda)
        {
            isVenda = false;
            try
            {
                if (ema9 == null || ema30 == null) return 0;
                if (barsAgo + 5 > CurrentBar) return 0;
                double tick = TickSize <= 0 ? 0.01 : TickSize;
                double e9 = ema9[barsAgo], e30 = ema30[barsAgo];
                double e9_5 = ema9[barsAgo + 5], e30_5 = ema30[barsAgo + 5];
                double e9_1 = ema9[barsAgo + 1], e9_2 = ema9[barsAgo + 2];
                bool alta = e9 > e30; isVenda = !alta;
                double slope9 = (e9 - e9_5) / tick, slope30 = (e30 - e30_5) / tick;
                double slopeMin = Math.Max(0.5, _estado.Sinal20_SlopeMin * 0.1);
                bool slope9OK = alta ? slope9 > slopeMin : slope9 < -slopeMin;
                bool slope30OK = alta ? slope30 > slopeMin : slope30 < -slopeMin;
                if (!slope9OK || !slope30OK) return 0;
                double distNow = Math.Abs(e9 - e30) / tick;
                double distAnt = Math.Abs(e9_1 - ema30[barsAgo + 1]) / tick;
                bool expandindo = distNow > distAnt;
                bool comprimindo = distNow < distAnt;
                if (comprimindo && distNow < _estado.Sinal20_CompressaoMin) return 0;
                double s9c = (e9 - e9_1) / tick, s9a = (e9_1 - e9_2) / tick;
                bool acel = alta ? s9c > s9a : s9c < s9a;
                int score = 20;
                if (slope9OK) score += 20;
                if (slope30OK) score += 20;
                if (expandindo) score += 15;
                if (acel) score += 10;
                // pullback + rompimento simplificados (peso menor) para o passado
                score += 10;
                return Math.Max(0, Math.Min(100, score));
            }
            catch { isVenda = false; return 0; }
        }

        // Resolve gain/stop de um sinal em barsAgo varrendo as barras seguintes (memória).
        // Limitado a ~60 barras à frente para não virar O(n²) no reprocessamento.
        private int ResolverGainStopEm(int barsAgoSinal, bool isVenda, double stop, double alvo)
        {
            try
            {
                int limite = Math.Max(0, barsAgoSinal - 60);   // no máx. 60 barras à frente
                for (int i = barsAgoSinal - 1; i >= limite; i--)
                {
                    double hi = High[i], lo = Low[i];
                    if (!isVenda)
                    {
                        if (lo <= stop) return -1;
                        if (hi >= alvo) return 1;
                    }
                    else
                    {
                        if (hi >= stop) return -1;
                        if (lo <= alvo) return 1;
                    }
                }
                return 0;
            }
            catch { return 0; }
        }

        #region --- SISTEMA DE DRAG & DROP & HOVER ---
        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Arrasto do mini painel estatístico (independe do dashboard principal).
            if (MostrarPainelEstatistico && _painelEstRect.Width > 0)
            {
                System.Windows.Point ptp = e.GetPosition(ChartControl);
                // só a barra de título (topo 24px) inicia o arrasto
                if (ptp.X >= _painelEstRect.Left && ptp.X <= _painelEstRect.Right &&
                    ptp.Y >= _painelEstRect.Top && ptp.Y <= _painelEstRect.Top + 24f)
                {
                    _arrastandoPainel = true;
                    _arrasteDX = (float)ptp.X - _painelEstRect.Left;
                    _arrasteDY = (float)ptp.Y - _painelEstRect.Top;
                    e.Handled = true;
                    return;
                }
            }

            if (!MostrarDashboard || engine == null) return;

            System.Windows.Point pt = e.GetPosition(ChartControl);

            // Clique em FULL → modo completo. Clique em BASIC → modo minimalista.
            if (engine.botaoFullRect.Width > 0 &&
                pt.X >= engine.botaoFullRect.Left && pt.X <= engine.botaoFullRect.Right &&
                pt.Y >= engine.botaoFullRect.Top && pt.Y <= engine.botaoFullRect.Bottom)
            {
                _estado.ModoFull = true;
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (engine.botaoBasicRect.Width > 0 &&
                pt.X >= engine.botaoBasicRect.Left && pt.X <= engine.botaoBasicRect.Right &&
                pt.Y >= engine.botaoBasicRect.Top && pt.Y <= engine.botaoBasicRect.Bottom)
            {
                _estado.ModoFull = false;
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (engine.botaoSinaisRect.Width > 0 &&
                pt.X >= engine.botaoSinaisRect.Left && pt.X <= engine.botaoSinaisRect.Right &&
                pt.Y >= engine.botaoSinaisRect.Top && pt.Y <= engine.botaoSinaisRect.Bottom)
            {
                _estado.StatAberto = !_estado.StatAberto;
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (engine.botaoModoRect.Width > 0 &&
                pt.X >= engine.botaoModoRect.Left && pt.X <= engine.botaoModoRect.Right &&
                pt.Y >= engine.botaoModoRect.Top && pt.Y <= engine.botaoModoRect.Bottom)
            {
                // alterna Conservador (✕ cancelado) ↔ Agressivo (sem cancelamento).
                _estado.ModoConservador = !_estado.ModoConservador;
                ReprocessarSinais();
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            // X → fecha o dashboard (esconde o painel; sinais no gráfico permanecem)
            if (engine.botaoFecharDashRect.Width > 0 &&
                pt.X >= engine.botaoFecharDashRect.Left && pt.X <= engine.botaoFecharDashRect.Right &&
                pt.Y >= engine.botaoFecharDashRect.Top && pt.Y <= engine.botaoFecharDashRect.Bottom)
            {
                _estado.DashboardVisivel = false;
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            // − → minimiza o dashboard (barra compacta)
            if (engine.botaoMinimizarRect.Width > 0 &&
                pt.X >= engine.botaoMinimizarRect.Left && pt.X <= engine.botaoMinimizarRect.Right &&
                pt.Y >= engine.botaoMinimizarRect.Top && pt.Y <= engine.botaoMinimizarRect.Bottom)
            {
                _estado.DashboardMinimizado = !_estado.DashboardMinimizado;
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (_estado.StatAberto && engine.statFecharRect.Width > 0 &&
                pt.X >= engine.statFecharRect.Left && pt.X <= engine.statFecharRect.Right &&
                pt.Y >= engine.statFecharRect.Top && pt.Y <= engine.statFecharRect.Bottom)
            {
                _estado.StatAberto = false;
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (engine.botaoSinal20Rect.Width > 0 &&
                pt.X >= engine.botaoSinal20Rect.Left && pt.X <= engine.botaoSinal20Rect.Right &&
                pt.Y >= engine.botaoSinal20Rect.Top && pt.Y <= engine.botaoSinal20Rect.Bottom)
            {
                // ativa o Sinal 2.0 (tendência ancorada em EMA) e desliga o 1.0
                if (!_estado.Sinal20)
                {
                    _estado.Sinal20 = true;
                    _estado.Sinal10 = false;
                    ReprocessarSinais();
                }
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (engine.botaoSinal10Rect.Width > 0 &&
                pt.X >= engine.botaoSinal10Rect.Left && pt.X <= engine.botaoSinal10Rect.Right &&
                pt.Y >= engine.botaoSinal10Rect.Top && pt.Y <= engine.botaoSinal10Rect.Bottom)
            {
                // ativa o Sinal 1.0 (regiões) e desliga o 2.0
                if (_estado.Sinal20)
                {
                    _estado.Sinal10 = true;
                    _estado.Sinal20 = false;
                    ReprocessarSinais();
                }
                ChartControl.InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (pt.X >= PainelDeslocamentoX && pt.X <= PainelDeslocamentoX + DashboardEngine.PanelWidth && 
                pt.Y >= PainelDeslocamentoY && pt.Y <= PainelDeslocamentoY + DashboardEngine.PanelHeight)
            {
                _isDragging = true;
                _dragStartPoint = pt;
                _panelStartDragX = PainelDeslocamentoX;
                _panelStartDragY = PainelDeslocamentoY;
                e.Handled = true; 
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (ChartControl == null) return;
            System.Windows.Point pt = e.GetPosition(ChartControl);
            _mouseX = (float)pt.X;
            _mouseY = (float)pt.Y;

            // arrasto do mini painel estatístico
            if (_arrastandoPainel)
            {
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                {
                    _arrastandoPainel = false;
                }
                else
                {
                    _painelEstX = (float)pt.X - _arrasteDX;
                    _painelEstY = (float)pt.Y - _arrasteDY;
                    try { ChartControl.InvalidateVisual(); } catch { }
                    return;
                }
            }

            if (_isDragging)
            {
                double diffX = pt.X - _dragStartPoint.X;
                double diffY = pt.Y - _dragStartPoint.Y;

                PainelDeslocamentoX = (float)(_panelStartDragX + diffX);
                PainelDeslocamentoY = (float)(_panelStartDragY + diffY);

                // Movimento livre por todo o gráfico, sem travar nas bordas.
                // Único limite: mantém uma faixa mínima do painel sempre acessível
                // (para nunca perder a barra de arraste ao empurrar para fora).
                float margemMin = 40f;
                if (ChartControl != null)
                {
                    float larguraTela = (float)ChartControl.ActualWidth;
                    float alturaTela = (float)ChartControl.ActualHeight;
                    if (PainelDeslocamentoX > larguraTela - margemMin) PainelDeslocamentoX = larguraTela - margemMin;
                    if (PainelDeslocamentoX < -(DashboardEngine.PanelWidth - margemMin)) PainelDeslocamentoX = -(DashboardEngine.PanelWidth - margemMin);
                    if (PainelDeslocamentoY > alturaTela - margemMin) PainelDeslocamentoY = alturaTela - margemMin;
                    if (PainelDeslocamentoY < 0) PainelDeslocamentoY = 0; // topo sempre pega a barra de arraste
                }
            }
            
            if (MostrarDashboard && engine != null)
            {
                ChartControl.InvalidateVisual();
            }
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _arrastandoPainel = false;
        }
        #endregion

        // Detecta o timeframe do gráfico e aplica um preset de parâmetros adequado.
        // Converte tudo para "segundos equivalentes" para classificar em faixas.
        private void AplicarPresetPorTimeframe()
        {
            try
            {
                int val = BarsPeriod.Value;
                double segundos;
                switch (BarsPeriod.BarsPeriodType)
                {
                    case BarsPeriodType.Second: segundos = val; break;
                    case BarsPeriodType.Minute: segundos = val * 60.0; break;
                    case BarsPeriodType.Day:    segundos = val * 86400.0; break;
                    default: segundos = val * 60.0; break;  // tick/volume/range: trata como "rápido"
                }

                // Faixas de calibração. Gráficos mais rápidos → swings menores, timing mais ágil,
                // zonas mais sensíveis. Mais lentos → swings maiores, mais robustez.
                if (segundos <= 45)            // ultra-rápido (≤45s)
                {
                    SwingStrength = 3;
                    BarrasConsecutivasDelta = 1;
                    DeltaMediaPeriodo = 2;
                    ScoreMinimoSinal = 65;
                    EmaTendenciaLookback = 6;
                }
                else if (segundos <= 90)       // rápido (1min–1.5min)
                {
                    SwingStrength = 4;
                    BarrasConsecutivasDelta = 1;
                    DeltaMediaPeriodo = 2;
                    ScoreMinimoSinal = 68;
                    EmaTendenciaLookback = 7;
                }
                else if (segundos <= 180)      // médio (2min–3min)
                {
                    SwingStrength = 5;
                    BarrasConsecutivasDelta = 2;
                    DeltaMediaPeriodo = 3;
                    ScoreMinimoSinal = 70;
                    EmaTendenciaLookback = 8;
                }
                else if (segundos <= 600)      // padrão (5min–10min)
                {
                    SwingStrength = 6;
                    BarrasConsecutivasDelta = 2;
                    DeltaMediaPeriodo = 3;
                    ScoreMinimoSinal = 72;
                    EmaTendenciaLookback = 9;
                }
                else                            // lento (15min+)
                {
                    SwingStrength = 8;
                    BarrasConsecutivasDelta = 3;
                    DeltaMediaPeriodo = 4;
                    ScoreMinimoSinal = 75;
                    EmaTendenciaLookback = 12;
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ETAPA 1 — DELTA REAL tick a tick.
        // Roda a cada tick de mercado (independente do modo Calculate). Classifica
        // cada negócio (Last) contra bid/ask: preço no ask → comprador (+vol),
        // preço no bid → vendedor (−vol). Guarda o delta da barra e seus extremos
        // (min/max) — o min/max é o que permite detectar o FLIP nas próximas etapas.
        // ═══════════════════════════════════════════════════════════════════════
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!usarDeltaReal) return;

            // atualiza o melhor bid/ask correntes
            if (e.MarketDataType == MarketDataType.Ask) { currentAsk = e.Ask; return; }
            if (e.MarketDataType == MarketDataType.Bid) { currentBid = e.Bid; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            // zera o acumulador quando entra uma barra nova
            if (CurrentBar != deltaBarIndex)
            {
                deltaBarIndex = CurrentBar;
                barDeltaReal = barMinDelta = barMaxDelta = 0;
            }

            double vol = e.Volume;
            // classificação por agressão: negócio no ask (ou acima) = compra; no bid (ou abaixo) = venda
            if (!double.IsNaN(currentAsk) && e.Price >= currentAsk)      barDeltaReal += vol;
            else if (!double.IsNaN(currentBid) && e.Price <= currentBid) barDeltaReal -= vol;

            // extremos intra-barra (essenciais para o FLIP: saber se o delta afundou antes de virar)
            if (barDeltaReal < barMinDelta) barMinDelta = barDeltaReal;
            if (barDeltaReal > barMaxDelta) barMaxDelta = barDeltaReal;

            // ── FITA: empilha o timestamp e descarta o que saiu da janela ──
            long agora = e.Time.Ticks;
            tapeTs[tapeHead] = agora;
            tapeHead = (tapeHead + 1) % TAPE_CAP;
            if (tapeCount < TAPE_CAP) tapeCount++; else tapeTail = (tapeTail + 1) % TAPE_CAP;
            long corte = agora - TimeSpan.FromMilliseconds(TapeWindowMs).Ticks;
            while (tapeCount > 0 && tapeTs[tapeTail] < corte)
            {
                tapeTail = (tapeTail + 1) % TAPE_CAP;
                tapeCount--;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;

            if (!_licencaValida)
            {
                if (_licencaChecada && IsFirstTickOfBar)
                {
                    try
                    {
                        Draw.TextFixed(this, "LicencaStatus",
                            "PROFIT ACADEMY PRO — " + _licencaMensagem,
                            TextPosition.BottomRight,
                            System.Windows.Media.Brushes.White,
                            new SimpleFont("Segoe UI", 12),
                            System.Windows.Media.Brushes.Transparent,
                            System.Windows.Media.Brushes.Firebrick, 80,
                            DashStyleHelper.Solid, 1,
                            false, null);
                    }
                    catch { }
                }
                return;
            }

            // ══════════════════════════════════════════════════════════════════
            // MODO DE PROCESSAMENTO:
            // - Tempo real: processa tudo (dashboard + sinais + reflection Dipcorp).
            // - Histórico: só processa se "Mostrar Sinais Históricos" estiver ligado,
            //   e mesmo assim de forma LEVE (sem reflection Dipcorp, sem métricas do
            //   painel) — apenas calcula e plota os sinais passados para backtest visual.
            //   Sem essa opção, o histórico é ignorado (carrega leve, não trava).
            // ══════════════════════════════════════════════════════════════════
            bool emTempoReal = State == State.Realtime;

            // Ao entrar em tempo real pela 1ª vez, avalia o win/loss de todos os sinais
            // históricos registrados (todas as barras já existem) → placar já vem preenchido.
            if (emTempoReal && !_historicoAvaliado)
            {
                _historicoAvaliado = true;
                if (MostrarPainelEstatistico) AvaliarTodosHistorico();
            }
            // Histórico sempre processado (registra as marcas p/ render SharpDX). Como o
            // desenho agora é via SharpDX (leve) e há teto de sinais, não trava o load.
            bool modoHistorico = !emTempoReal;
            if (!emTempoReal && !modoHistorico)
                return;

            // conta barras processadas em tempo real (só no 1º tick de cada barra)
            if (emTempoReal && IsFirstTickOfBar) _barsRealtime++;

            bool barraRecente = true;   // sempre processa quando chega aqui

            // ── MODO DE MARCAÇÃO DO SINAL ──
            // SinalNoTick = false → processa a lógica de sinais apenas no FECHAMENTO
            //   (no 1º tick da barra nova, quando a anterior já fechou).
            // SinalNoTick = true  → processa a cada tick (marca no instante do disparo).
            // O dashboard/UI é sempre atualizado; só a EMISSÃO respeita este modo.
            bool avaliarSinalAgora = SinalNoTick || IsFirstTickOfBar;

            // No modo histórico leve, pula toda a parte de dashboard/config e vai direto
            // para o cálculo e plotagem dos sinais (mais abaixo).
            if (modoHistorico) { ProcessarSinalHistorico(); return; }

            // Aplica as configurações salvas no dashboard aos parâmetros reais do indicador,
            // conforme o modo de sinal ativo.
            if (_estado.Sinal20)
            {
                ScoreMinimoSinal = _estado.Cfg20_ScoreMin;
                MinDivergencesRequired = _estado.Cfg20_MinDivergencias;
                UsarExaustaoFluxo = _estado.Cfg20_UsarExaustao;
                PriorizarSR = _estado.Cfg20_PriorizarSR;
                AceitarGatilhoNaZona = _estado.Cfg20_ExigirZona;
                ExigirBopEDelta = _estado.Cfg20_ExigirDelta;
            }
            else if (_estado.Sinal30)
            {
                ScoreMinimoSinal = _estado.Cfg30_ScoreMin;
                ExigirGatilhoTiming = _estado.Cfg30_ExigirGatilho;
                FiltrarContraTendencia = _estado.Cfg30_FiltrarContraTend;
            }
            else if (_estado.Sinal10)
            {
                ScoreMinimoSinal = _estado.Cfg10_ScoreMin;
                EmaPeriodo = _estado.Cfg10_EmaPeriodo;
                SwingStrength = _estado.Cfg10_SwingStrength;
                FiltrarContraTendencia = _estado.Cfg10_ApenasTendencia;
            }

            // ── SINAL 4.0: liga a estratégia FLIP institucional completa ──
            // (forma+fluxo+fita+risco+R:R + gates macro60/ExR/tipo). Ativa os gates
            // que por padrão vêm desligados, sem o usuário precisar mexer manualmente.
            if (_estado.Sinal40)
            {
                _estado.Flip_Ativo = true;
                _estado.Flip_ExigirFita = true;
                _estado.Flip_ExigirRR = true;
            }
            else
            {
                // Sai do 4.0 → desliga o gate flip para não afetar os outros modos.
                _estado.Flip_Ativo = false;
            }
            // Gerais
            AutoRegular = _estado.CfgGeral_AutoRegular;
            // Atualiza a tendência do 15min periodicamente (a cada ~5 min) em tempo real.
            if (FiltrarTendencia15min && State == State.Realtime
                && (DateTime.Now - _ultimaAtualizTend15).TotalMinutes >= 5)
            {
                AtualizarTendencia15();
            }
            // atualiza a cor do sinal parcial (caso o usuário mude em runtime)
            try
            {
                var scbP = CorSinalParcial as System.Windows.Media.SolidColorBrush;
                if (scbP != null) { _corParcialR = scbP.Color.R; _corParcialG = scbP.Color.G; _corParcialB = scbP.Color.B; }
                var scbC2 = CorSinalCancelado as System.Windows.Media.SolidColorBrush;
                if (scbC2 != null) { _corCancR = scbC2.Color.R; _corCancG = scbC2.Color.G; _corCancB = scbC2.Color.B; }
                var scbV2 = TopDivergenceBrush as System.Windows.Media.SolidColorBrush;
                if (scbV2 != null) { _corVendaR = scbV2.Color.R; _corVendaG = scbV2.Color.G; _corVendaB = scbV2.Color.B; }
                var scbCp2 = BottomDivergenceBrush as System.Windows.Media.SolidColorBrush;
                if (scbCp2 != null) { _corCompraR = scbCp2.Color.R; _corCompraG = scbCp2.Color.G; _corCompraB = scbCp2.Color.B; }
            }
            catch { }
            AnimacoesLeves = _estado.CfgGeral_Animacoes;

            // ── CAMADA 1: avalia contexto/qualidade de mercado a cada barra ──
            AvaliarContextoMercado();

            // ── ETAPA 1: expõe o delta real no painel (só em barras recentes) ──
            if (engine != null && barraRecente)
            {
                bool volumetrico = (Bars.BarsSeries.BarsType as NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType) != null;
                engine.Metrics.DeltaReal = deltaSeries[0];
                engine.Metrics.DeltaMin = barMinDelta;
                engine.Metrics.DeltaMax = barMaxDelta;
                engine.Metrics.DeltaRealAtivo = volumetrico || (usarDeltaReal && State == State.Realtime && !double.IsNaN(currentBid));

                // Etapa 2: status e confluência das zonas Dipcorp (reflection — caro)
                engine.Metrics.DipcorpAtivo = _dipcorpOk;
                if (_dipcorpOk)
                    engine.Metrics.DipcorpConfluenciaZona = Math.Max(DipcorpConfluencia(High[0]), DipcorpConfluencia(Low[0]));
            }

            // ── ETAPA 4: atualiza o ExR (só em barras recentes) ──
            if (barraRecente) AtualizarExR();

            // Se o modo Sinal 2.0 foi alternado, remove os sinais do modo OPOSTO ao atual,
            // deixando o gráfico só com os sinais do modo agora ativo.
            // Troca de estratégia (1.0/2.0) OU de modo (Conservador/Agressivo) →
            // reprocessa o histórico do zero SEM F5 (fallback: caso a flag tenha sido
            // setada por algum caminho que não chamou ReprocessarSinais diretamente).
            if (_estado.Sinal20Mudou || _estado.ModoMudou)
            {
                _estado.Sinal20Mudou = false;
                _estado.ModoMudou = false;
                ReprocessarSinais();   // limpa e recalcula EM MEMÓRIA (sem reload/F5)
            }

            if (CurrentBar < Math.Max(32, Math.Max(SwingStrength * 2, Math.Max(Math.Max(AdxPeriodo, EmaPeriodo), Math.Max(RsiPeriod, StochPeriodK) + 5)))) return;

            // S&D e swings dependem de barras fechadas → recalcula só no 1º tick da barra
            // (evita custo desnecessário a cada tick no modo tempo real).
            if (IsFirstTickOfBar || CurrentBar != _ultimoBarSD)
            {
                UpdateSupplyDemand();
                EvaluateSwings();
                _ultimoBarSD = CurrentBar;

                // Volume Profile do range de consolidação: recalcula quando o preço está
                // lateralizado, para ter POC/VAH/VAL atualizados do range atual.
                if (UsarFiltroVolumeProfile && (CurrentBar - _vpUltimoCalculo) >= 3)
                {
                    int bi, bf;
                    if (EmConsolidacao(VpJanelaBarras, out bi, out bf))
                    {
                        CalcularVolumeProfile(bi, bf);
                        _vpUltimoCalculo = CurrentBar;
                    }
                    else _vpValido = false;   // sem consolidação → sem profile válido
                }
            }

            // Séries de delta/BOP atualizam a cada tick (refletem o fluxo do tick atual).
            CalculateInternalSeries();

            // Avalia win/loss dos sinais pendentes (na barra seguinte ao sinal).
            if (MostrarPainelEstatistico && IsFirstTickOfBar) AvaliarResultadosPendentes();

            // aguarda algumas barras em tempo real para as séries customizadas
            // (deltaSeries[1], [2], bopSmoothed) terem valores válidos antes de
            // calcular divergências — evita acesso a índice inválido no início.
            if (_barsRealtime < 3) return;

            // ============================================================
            // SINAL 1.0 — TENDÊNCIA / BIPOLARIDADE (novo motor)
            // ============================================================
            // Rastreia agressão na zona e emite quando a confluência (região + EMA9
            // + estocástico extremo K×D + delta/agressão) atinge o limiar.
            {
                bool inSupply = IsInActiveSupplyZone(High[0]) || IsInActiveSupplyZone(Close[0]);
                bool inDemand = IsInActiveDemandZone(Low[0]) || IsInActiveDemandZone(Close[0]);
                AtualizarRastreadorZona(inSupply, inDemand);

                int score10; bool isVenda10; string conf10;

                // ── MODO "USAR AMBAS" ──
                // Avalia 1.0 e 2.0 SEPARADAMENTE e emite as duas quando disparam.
                // Prioridade: a 1.0 ocupa a posição normal; a 2.0 aparece deslocada logo
                // atrás (para não sobrepor). Cada uma tem seu próprio controle de barra.
                if (UsarAmbasEstrategias)
                {
                    // ---- Estratégia 1.0 (regiões) ----
                    int sc1; bool venda1; string cf1;
                    bool cand1 = AvaliarSinalPadrao(out sc1, out venda1, out cf1);
                    if (cand1 && SomenteNasZonas && !PrecoEmZonaLiquidez(venda1)) cand1 = false;
                    bool contra1 = false;
                    if (cand1 && FiltrarTendencia15min && _tend15Pronta)
                    {
                        if (venda1 && _tendencia15 == 1) contra1 = true;
                        if (!venda1 && _tendencia15 == -1) contra1 = true;
                    }
                    if (avaliarSinalAgora && cand1 && CurrentBar != ultimoBarSinal)
                    {
                        bool comp1 = sc1 >= 90 && !contra1;
                        if (comp1 || MostrarSinaisParciais)
                        {
                            RemoverBolinhaPreSinal(CurrentBar);
                            _marcas.Add(new SinalMarca { bar = CurrentBar, venda = venda1, seta = true, hist = false, preco = Close[0], parcial = !comp1, formato = comp1 ? FormatoSinalCompleto : FormatoSinalParcial, high = High[0], low = Low[0] });
                            ultimoBarSinal = CurrentBar;
                            ultimaConfluencia = cf1;
                            RegistrarSinalCard(venda1);
                            RegistrarNoHistorico(venda1, comp1, "1.0", cf1, Close[0]);
                            _preSinalAtivo = false;
                            UpdateDashboardMetrics(true, (venda1 ? "VENDA" : "COMPRA") + (comp1 ? "" : " (parcial)"), inSupply || inDemand, comp1 ? 88.0 : 60.0, deltaSeries[0], 0);
                        }
                    }

                    // ---- Estratégia 2.0 (EMA) — deslocada logo atrás ----
                    int sc2; bool venda2; string cf2;
                    bool cand2 = AvaliarSinal20Padrao(out sc2, out venda2, out cf2);
                    if (cand2 && SomenteNasZonas && !PrecoEmZonaLiquidez(venda2)) cand2 = false;
                    bool contra2 = false;
                    if (cand2 && FiltrarTendencia15min && _tend15Pronta)
                    {
                        if (venda2 && _tendencia15 == 1) contra2 = true;
                        if (!venda2 && _tendencia15 == -1) contra2 = true;
                    }
                    if (avaliarSinalAgora && cand2 && CurrentBar != _ultimoBar20)
                    {
                        bool comp2 = sc2 >= 90 && !contra2;
                        if (comp2 || MostrarSinaisParciais)
                        {
                            // marca deslocada (offset20=true) para não sobrepor a 1.0
                            _marcas.Add(new SinalMarca { bar = CurrentBar, venda = venda2, seta = true, hist = false, preco = Close[0], parcial = !comp2, formato = comp2 ? FormatoSinalCompleto : FormatoSinalParcial, offset20 = true, high = High[0], low = Low[0] });
                            _ultimoBar20 = CurrentBar;
                            RegistrarNoHistorico(venda2, comp2, "2.0", cf2, Close[0]);
                        }
                    }

                    double probA = GaugeEstetico();
                    if (!cand1 && !cand2)
                        UpdateDashboardMetrics(false, ProbBias(), inSupply || inDemand, probA, deltaSeries[0], 0);

                    AtualizarCardSinal();
                    if (engine != null) engine.Metrics.IaMensagem = AnalisarBrigaRegiao();
                    goto FimBlocoSinais;   // pula o fluxo de estratégia única
                }

                // alterna o motor conforme o botão do dashboard:
                // Sinal 2.0 = tendência ancorada em EMA · Sinal 1.0 = regiões.
                bool candidato = _estado.Sinal20
                    ? AvaliarSinal20Padrao(out score10, out isVenda10, out conf10)
                    : AvaliarSinalPadrao(out score10, out isVenda10, out conf10);
                int limiar = _estado.Padrao_ScoreMin;

                // ── FILTRO: SÓ OPERAR DENTRO DAS ZONAS ──
                // Se ligado, o preço precisa estar numa zona de liquidez (supply/demand/
                // bipolaridade) para o sinal valer. Fora das zonas, ignora tudo.
                if (candidato && SomenteNasZonas && !PrecoEmZonaLiquidez(isVenda10))
                    candidato = false;

                // ── FILTRO DE VOLUME PROFILE ──
                // Só deixa passar sinais nas BORDAS da Value Area (VAH/VAL) ou no POC —
                // tanto do range de consolidação quanto das zonas (pivôs/S&R/supply&demand).
                if (candidato && UsarFiltroVolumeProfile && (_vpValido || VpNasZonas)
                    && !NaBordaValueArea(Close[0]) && !NaBordaValueArea(isVenda10 ? High[0] : Low[0]))
                    candidato = false;

                // ── FILTRO DE TENDÊNCIA DO 15 MIN ──
                // NÃO corta o sinal: se ele está CONTRA a tendência do 15min, apenas o
                // REBAIXA (impede que seja "completo/100%"). Assim o sinal ainda aparece,
                // mas nunca colorido como completo quando vai contra a tendência maior.
                bool contraTend15 = false;
                if (candidato && FiltrarTendencia15min && _tend15Pronta)
                {
                    if (isVenda10 && _tendencia15 == 1) contraTend15 = true;    // venda em tendência de alta
                    if (!isVenda10 && _tendencia15 == -1) contraTend15 = true;  // compra em tendência de baixa
                }

                // ── GAUGE ESTÉTICO (sem score real) ──
                // O anel do dashboard é apenas visual ("analisando"): oscila suavemente
                // em torno de um valor médio, sem representar probabilidade/score de trade.
                double probContinua = GaugeEstetico();

                // ── HIERARQUIA DE SINAIS ──
                // Sinal 2.0 (estrutura EMA): SETA ≥ 85 · BOLINHA 65-84 (regra do documento).
                // Sinal 1.0 (regiões): SETA > 65 · BOLINHA 55-65.
                double corteSeta = _estado.Sinal20 ? 85.0 : 65.0;
                double corteBolinhaMin = _estado.Sinal20 ? 65.0 : 55.0;

                // ── CLASSIFICAÇÃO DO SINAL ──
                // score10 >= 90  → TODAS as 3 confluências bateram → sinal COMPLETO (colorido)
                // score10 <  90  → 1 ou 2 confluências → sinal PARCIAL (cinza)
                // (o PLUS DIVERGÊNCIA também gera score 90 = completo/colorido)
                bool sinalCompleto = score10 >= 90;
                // Contra a tendência do 15min → nunca é completo (rebaixa para parcial).
                if (contraTend15) sinalCompleto = false;

                if (avaliarSinalAgora && candidato && CurrentBar != ultimoBarSinal
                    && (sinalCompleto || MostrarSinaisParciais))   // oculta parciais se desligado
                {
                    RemoverBolinhaPreSinal(CurrentBar);
                    string tag = "Sinal10_" + CurrentBar;

                    if (sinalCompleto)
                    {
                        // ── SINAL COMPLETO → cor de venda/compra · formato configurável ──
                        // O desenho é feito pelo render SharpDX (via _marcas), que suporta
                        // todos os formatos (seta/triângulo/bolinha/diamante/quadrado/cruz/estrela).
                        _marcas.Add(new SinalMarca { bar = CurrentBar, venda = isVenda10, seta = true, hist = false, preco = Close[0], parcial = false, formato = FormatoSinalCompleto, high = High[0], low = Low[0] });
                        UpdateDashboardMetrics(true, isVenda10 ? "VENDA" : "COMPRA", inSupply || inDemand, 88.0, deltaSeries[0], 0);
                    }
                    else
                    {
                        // ── SINAL PARCIAL → cor parcial · formato configurável ──
                        _marcas.Add(new SinalMarca { bar = CurrentBar, venda = isVenda10, seta = true, hist = false, preco = Close[0], parcial = true, formato = FormatoSinalParcial, high = High[0], low = Low[0] });
                        UpdateDashboardMetrics(true, isVenda10 ? "VENDA (parcial)" : "COMPRA (parcial)", inSupply || inDemand, 60.0, deltaSeries[0], 0);
                    }

                    ultimoBarSinal = CurrentBar;
                    tagsSinaisNormais.Add(tag);
                    ultimaConfluencia = conf10;
                    RegistrarSinalCard(isVenda10);
                    RegistrarNoHistorico(isVenda10, sinalCompleto, _estado.Sinal20 ? "2.0" : "1.0", conf10, Close[0]);
                    _preSinalAtivo = false;
                }
                else
                {
                    string biasProb = ProbBias();
                    UpdateDashboardMetrics(false, biasProb, inSupply || inDemand, probContinua, deltaSeries[0], 0);
                }

                AtualizarCardSinal();
                if (engine != null) engine.Metrics.IaMensagem = AnalisarBrigaRegiao();

                FimBlocoSinais: ;
            }


            if ((MostrarDashboard || UsarPainelFlutuante) && engine != null)
            {
                engine.Metrics.CurrentDelta = deltaSeries[0];
                // dados da estatística de assertividade (painel SINAIS)
                engine.StatGains = _statGains;
                engine.StatStops = _statStops;
                engine.StatPontos = TicksAlvoStat;
                engine.StatStopTicks = TicksStopStat;
                engine.StatPontosGanhos = _statPontosGanhos;
                engine.StatPontosPerdidos = _statPontosPerdidos;
                engine.StatCompras = _statCompras;
                engine.StatVendas = _statVendas;
            }
        }

        public override void OnRenderTargetChanged()
        {
            if (engine != null && RenderTarget != null)
            {
                engine.OnRenderTargetChanged(RenderTarget);
            }
        }

        #region Helpers de Lookback Seguros
        private double GetMax(int period, int barsAgo = 0)
        {
            double max = double.MinValue;
            int limit = Math.Min(CurrentBar, barsAgo + period - 1);
            for(int i = barsAgo; i <= limit; i++)
            {
                if (High[i] > max) max = High[i];
            }
            return max != double.MinValue ? max : High[0];
        }

        private double GetMin(int period, int barsAgo = 0)
        {
            double min = double.MaxValue;
            int limit = Math.Min(CurrentBar, barsAgo + period - 1);
            for(int i = barsAgo; i <= limit; i++)
            {
                if (Low[i] < min) min = Low[i];
            }
            return min != double.MaxValue ? min : Low[0];
        }
        #endregion

        #region Lógica Institucional (Supply & Demand)
        // ── ZONAS MULTI-TIMEFRAME ──
        // Calcula zonas de supply/demand na SÉRIE SECUNDÁRIA (timeframe escolhido em
        // TimeframeZonaMin) e as adiciona à lista Zones, que é compartilhada com todo o
        // resto do indicador. Assim os sinais usam as zonas do TF que você pediu.
        // Usa pivôs simples (fractal de 3 barras) e monta a zona no corpo do candle-pivô.
        // ── ZONAS MULTI-TIMEFRAME via BarsRequest ──
        // Busca o histórico completo do timeframe escolhido (TimeframeZonaMin) e calcula
        // TODAS as zonas de supply/demand de uma vez, guardando em _zonasMTF. Rodar de
        // uma vez (não intercalado como AddDataSeries) garante que as zonas existam
        // quando qualquer sinal — do passado ou presente — for avaliado.
        private void DispararBarsRequestZonas()
        {
            try
            {
                _zonasMTF.Clear();
                _zonasMTFProntas = false;

                DateTime fim = DateTime.Now;
                DateTime inicio = fim.AddDays(-30);
                var req = new NinjaTrader.Data.BarsRequest(Instrument, inicio, fim);
                req.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = TimeframeZonaMin };
                req.TradingHours = Bars.TradingHours;

                // Request SÍNCRONO: bloqueia até as barras chegarem, garantindo que as
                // zonas existam ANTES do histórico processar os sinais (sem reload/loop).
                var evento = new System.Threading.ManualResetEvent(false);
                req.Request((request, errorCode, errorMessage) =>
                {
                    try
                    {
                        if (errorCode == NinjaTrader.Cbi.ErrorCode.NoError && request != null && request.Bars != null)
                            CalcularZonasDoBarsRequest(request.Bars);
                    }
                    catch { }
                    finally { try { evento.Set(); } catch { } }
                });

                // espera no máx. 8s para não travar caso o provedor demore
                evento.WaitOne(8000);
                _zonasMTFProntas = true;
                try { req.Dispose(); } catch { }
            }
            catch { _zonasMTFProntas = true; }
        }

        // ── FILTRO DE TENDÊNCIA DO 15 MIN ──
        // Busca as barras do 15min via BarsRequest e define o viés pela EMA 9 vs EMA 30:
        // EMA9 > EMA30 e subindo → alta (1) · EMA9 < EMA30 e caindo → baixa (-1) · senão neutro (0).
        private void AtualizarTendencia15()
        {
            try
            {
                _tend15Pronta = false;
                DateTime fim = DateTime.Now;
                DateTime inicio = fim.AddDays(-10);
                var req = new NinjaTrader.Data.BarsRequest(Instrument, inicio, fim);
                req.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 15 };
                req.TradingHours = Bars.TradingHours;

                var evento = new System.Threading.ManualResetEvent(false);
                req.Request((request, errorCode, errorMessage) =>
                {
                    try
                    {
                        if (errorCode == NinjaTrader.Cbi.ErrorCode.NoError && request != null && request.Bars != null)
                            _tendencia15 = CalcularTendenciaBars(request.Bars);
                    }
                    catch { }
                    finally { try { evento.Set(); } catch { } }
                });

                evento.WaitOne(8000);
                _tend15Pronta = true;
                _ultimaAtualizTend15 = DateTime.Now;
                try { req.Dispose(); } catch { }
            }
            catch { _tend15Pronta = true; }
        }

        // Calcula a tendência de uma série de barras via EMA 9/30 (viés: 1/-1/0).
        private int CalcularTendenciaBars(NinjaTrader.Data.Bars bars)
        {
            try
            {
                int n = bars.Count;
                if (n < 35) return 0;

                // EMA manual (9 e 30) sobre os closes da série do 15min
                double k9 = 2.0 / (9 + 1), k30 = 2.0 / (30 + 1);
                double ema9v = bars.GetClose(0), ema30v = bars.GetClose(0);
                double ema9ant = ema9v;
                for (int i = 1; i < n; i++)
                {
                    double c = bars.GetClose(i);
                    ema9ant = ema9v;
                    ema9v = c * k9 + ema9v * (1 - k9);
                    ema30v = c * k30 + ema30v * (1 - k30);
                }

                bool alta = ema9v > ema30v && ema9v >= ema9ant;
                bool baixa = ema9v < ema30v && ema9v <= ema9ant;
                if (alta) return 1;
                if (baixa) return -1;
                return 0;
            }
            catch { return 0; }
        }

        // Percorre as barras do TF escolhido e monta as zonas (pivô de 3 barras + ATR).
        private void CalcularZonasDoBarsRequest(NinjaTrader.Data.Bars bars)
        {
            try
            {
                int n = bars.Count;
                if (n < 10) return;
                double tick = TickSize <= 0 ? 0.01 : TickSize;

                // ATR médio simples do TF
                double atr = 0; int cA = 0;
                for (int i = 1; i < Math.Min(n, 30); i++) { atr += (bars.GetHigh(i) - bars.GetLow(i)); cA++; }
                atr = cA > 0 ? atr / cA : tick * 4;
                if (atr <= 0) atr = tick * 4;

                // varre procurando pivôs (3 barras de cada lado)
                for (int i = 3; i < n - 3; i++)
                {
                    double h = bars.GetHigh(i), l = bars.GetLow(i);
                    double o = bars.GetOpen(i), c = bars.GetClose(i);
                    bool topo =
                        h >= bars.GetHigh(i-1) && h >= bars.GetHigh(i-2) && h >= bars.GetHigh(i-3) &&
                        h >= bars.GetHigh(i+1) && h >= bars.GetHigh(i+2) && h >= bars.GetHigh(i+3);
                    bool fundo =
                        l <= bars.GetLow(i-1) && l <= bars.GetLow(i-2) && l <= bars.GetLow(i-3) &&
                        l <= bars.GetLow(i+1) && l <= bars.GetLow(i+2) && l <= bars.GetLow(i+3);

                    DateTime t = bars.GetTime(i);
                    int barPrim = CurrentBar;
                    try { int bp = BarsArray[0].GetBar(t); if (bp > 0) barPrim = bp; } catch { }

                    if (topo)
                    {
                        double zh = h;
                        double zl = Math.Min(Math.Max(o, c), zh - atr * 0.5);
                        if (zh - zl < tick) zl = zh - tick;
                        if (!ZonaMTFDuplicada(zl, zh, "s"))
                        {
                            var zN = new Zone(zl, zh, barPrim, "s", "r", true);
                            double vp1, vp2, vp3;
                            if (VolumeProfileDeBars(bars, i - 3, i + 3, out vp1, out vp2, out vp3)) { zN.poc = vp1; zN.vah = vp2; zN.val = vp3; }
                            _zonasMTF.Add(zN);
                        }
                    }
                    if (fundo)
                    {
                        double zl = l;
                        double zh = Math.Max(Math.Min(o, c), zl + atr * 0.5);
                        if (zh - zl < tick) zh = zl + tick;
                        if (!ZonaMTFDuplicada(zl, zh, "d"))
                        {
                            var zN = new Zone(zl, zh, barPrim, "d", "r", true);
                            double vp1, vp2, vp3;
                            if (VolumeProfileDeBars(bars, i - 3, i + 3, out vp1, out vp2, out vp3)) { zN.poc = vp1; zN.vah = vp2; zN.val = vp3; }
                            _zonasMTF.Add(zN);
                        }
                    }
                }
                _zonasMTFCriadas = _zonasMTF.Count;
            }
            catch { }
        }

        private bool ZonaMTFDuplicada(double zl, double zh, string tipo)
        {
            double meio = (zl + zh) / 2.0;
            foreach (var z in _zonasMTF)
            {
                if (z.t != tipo) continue;
                double zmeio = (z.l + z.h) / 2.0;
                if (Math.Abs(zmeio - meio) <= (z.h - z.l)) return true;
            }
            return false;
        }

        // Evita duplicar zonas muito próximas do mesmo tipo.
        private bool ZonaJaExiste(double zl, double zh, string tipo)
        {
            try
            {
                double meio = (zl + zh) / 2.0;
                foreach (var z in Zones)
                {
                    if (!z.a || z.t != tipo) continue;
                    double zmeio = (z.l + z.h) / 2.0;
                    if (Math.Abs(zmeio - meio) <= (z.h - z.l)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private void UpdateSupplyDemand()
        {
            // As zonas do gráfico atual são sempre calculadas em Zones. Quando um TF de
            // zona foi escolhido, o motor de sinais consulta _zonasMTF (via ZonasEmUso) e
            // ignora estas — mas mantê-las calculadas não atrapalha e evita estado vazio.

            if(isDnSwing(3)) { currHiBar = 3; currHiVal = High[3]; }
            if(isUpSwing(3)) { currLoBar = 3; currLoVal = Low[3]; }

            // ATR: usa o indicador da série primária quando disponível; no contexto MTF
            // (ou se ainda não pronto) usa um fallback baseado no range da série corrente.
            double atrBase;
            try { atrBase = atrInd[0]; }
            catch { atrBase = (High[0] - Low[0]); }
            if (double.IsNaN(atrBase) || atrBase <= 0) atrBase = Math.Max(TickSize, High[0] - Low[0]);
            atr = Instrument.MasterInstrument.RoundToTickSize(atrBase * 1.25);
            
            checkSupply();
            checkDemand();
            updateZonesSD();
            PruneZones();
            
            prevHiBar = currHiBar; prevHiVal = currHiVal;
            prevLoBar = currLoBar; prevLoVal = currLoVal;
        }

        // Remove zonas inativas mais antigas quando o total excede o limite, preservando todas as zonas ativas.
        // Evita crescimento ilimitado de memória/CPU em carregamentos históricos muito longos.
        private void PruneZones()
        {
            if (Zones.Count <= MaxStoredZones) return;

            int removeCount = Zones.Count - MaxStoredZones;
            int i = 0;
            while (i < Zones.Count && removeCount > 0)
            {
                // remove zonas inativas OU zonas rompidas já antigas (evita acúmulo infinito
                // agora que zonas rompidas permanecem ativas por bipolaridade)
                if (!Zones[i].a || (Zones[i].rompida && CurrentBar - Zones[i].b > MaxStoredZones * 4))
                {
                    Zones.RemoveAt(i);
                    removeCount--;
                }
                else
                {
                    i++;
                }
            }
        }

        private bool isUpSwing(int index)
        {
            return Low[index] <= Low[index-1] && Low[index] <= Low[index-2] && Low[index] <= Low[index-3] &&
                   Low[index] <= Low[index+1] && Low[index] <= Low[index+2] && Low[index] <= Low[index+3] &&
                   (Low[index] < Low[index-1] || Low[index] < Low[index-2] || Low[index] < Low[index-3]) &&
                   (Low[index] < Low[index+1] || Low[index] < Low[index+2] || Low[index] < Low[index+3]);
        }

        private bool isDnSwing(int index)
        {
            return High[index] >= High[index-1] && High[index] >= High[index-2] && High[index] >= High[index-3] &&
                   High[index] >= High[index+1] && High[index] >= High[index+2] && High[index] >= High[index+3] &&
                   (High[index] > High[index-1] || High[index] > High[index-2] || High[index] > High[index-3]) &&
                   (High[index] > High[index+1] || High[index] > High[index+2] || High[index] > High[index+3]);
        }

        private void checkSupply()
        {
            if(currHiVal != prevHiVal && GetMax(currHiBar + 1, 0) <= currHiVal)
            {
                if(!activeSupplyZoneExists(currHiVal) && isValidSupplyZone(currHiVal, currLoVal))
                {
                    double zr = High[currHiBar] - Math.Min(Open[currHiBar], Close[currHiBar]);
                    double zl = (zr > atr) ? Math.Max(Open[currHiBar], Close[currHiBar]) : Math.Min(Open[currHiBar], Close[currHiBar]);
                    double zh = currHiVal;
                    zl = (zh - zl < TickSize) ? (zh - TickSize) : zl;
                    var zNova = new Zone(zl, zh, CurrentBar - currHiBar, "s", "r", true);
                    { double vp1, vp2, vp3; if (CalcularVolumeProfileEm(currHiBar, currHiBar + SwingStrength, out vp1, out vp2, out vp3)) { zNova.poc = vp1; zNova.vah = vp2; zNova.val = vp3; } }
                    Zones.Add(zNova);
                }
            }

            int con = isDnContinuation();
            if(con != -1)
            {
                double hiCon = GetMax(con + 1, 0);
                double loCon = GetMin(con, 1);
                if(hiCon - loCon <= atr && !activeSupplyZoneExists(hiCon) && isValidSupplyZone(hiCon, loCon))
                {
                    double zl = loCon; 
                    double zh = hiCon; 
                    zl = (zh - zl < TickSize) ? (zh - TickSize) : zl;
                    var zNova = new Zone(zl, zh, CurrentBar - con, "s", "c", true);
                    { double vp1, vp2, vp3; if (CalcularVolumeProfileEm(con, con + 1, out vp1, out vp2, out vp3)) { zNova.poc = vp1; zNova.vah = vp2; zNova.val = vp3; } }
                    Zones.Add(zNova);
                }
            }
        }

        private void checkDemand()
        {
            if(currLoVal != prevLoVal && GetMin(currLoBar + 1, 0) >= currLoVal)
            {
                if(!activeDemandZoneExists(currLoVal) && isValidDemandZone(currHiVal, currLoVal))
                {
                    double zr = Math.Max(Open[currLoBar], Close[currLoBar]) - Low[currHiBar];
                    double zh = (zr > atr) ? Math.Min(Open[currLoBar], Close[currLoBar]) : Math.Max(Open[currLoBar], Close[currLoBar]);
                    double zl = currLoVal;
                    zh = (zh - zl < TickSize) ? (zl + TickSize) : zh;
                    var zNova = new Zone(zl, zh, CurrentBar - currLoBar, "d", "r", true);
                    { double vp1, vp2, vp3; if (CalcularVolumeProfileEm(currLoBar, currLoBar + SwingStrength, out vp1, out vp2, out vp3)) { zNova.poc = vp1; zNova.vah = vp2; zNova.val = vp3; } }
                    Zones.Add(zNova);
                }
            }

            int con = isUpContinuation();
            if(con != -1)
            {
                double hiCon = GetMax(con, 1);
                double loCon = GetMin(con + 1, 0);
                if(hiCon - loCon <= atr && !activeDemandZoneExists(loCon) && isValidDemandZone(hiCon, loCon))
                {
                    double zl = loCon; 
                    double zh = hiCon;
                    zh = (zh - zl < TickSize) ? (zl + TickSize) : zh;
                    var zNova = new Zone(zl, zh, CurrentBar - con, "d", "c", true);
                    { double vp1, vp2, vp3; if (CalcularVolumeProfileEm(con, con + 1, out vp1, out vp2, out vp3)) { zNova.poc = vp1; zNova.vah = vp2; zNova.val = vp3; } }
                    Zones.Add(zNova);
                }
            }
        }

        private bool activeSupplyZoneExists(double hi) { return Zones.Any(z => z.a && z.t == "s" && z.h == hi); }
        private bool activeDemandZoneExists(double lo) { return Zones.Any(z => z.a && z.t == "d" && z.l == lo); }

        // ── PERFIL DE VOLUME / POC ──
        // Calcula o Point of Control (faixa de preço de MAIOR volume) numa região de barras.
        // Funciona em QUALQUER gráfico:
        //  • Volumétrico: usa o volume real por nível de preço (preciso).
        //  • Normal (minuto/MTF): aproxima distribuindo o volume de cada barra pela sua
        //    faixa de preço (High-Low) em bins de 1 tick e acha o bin de maior volume.
        // barsAgoInicio/Fim são deslocamentos relativos à barra atual (0 = atual).
        private double CalcularPOC(int barsAgoInicio, int barsAgoFim)
        {
            try
            {
                int ini = Math.Min(barsAgoInicio, barsAgoFim);
                int fim = Math.Max(barsAgoInicio, barsAgoFim);
                double tick = TickSize <= 0 ? 0.01 : TickSize;

                var volBars = Bars.BarsSeries.BarsType as NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
                var volPorPreco = new System.Collections.Generic.Dictionary<double, double>();

                for (int ba = ini; ba <= fim; ba++)
                {
                    int abs = CurrentBar - ba;
                    if (abs < 0) continue;
                    double lo = Low[ba], hi = High[ba];
                    if (hi < lo) continue;

                    if (volBars != null)
                    {
                        // ---- Volumétrico: volume REAL por nível de preço ----
                        for (double p = lo; p <= hi + tick / 2.0; p += tick)
                        {
                            double preco = Instrument.MasterInstrument.RoundToTickSize(p);
                            long v = 0;
                            try { v = volBars.Volumes[abs].GetTotalVolumeForPrice(preco); } catch { }
                            if (v <= 0) continue;
                            if (volPorPreco.ContainsKey(preco)) volPorPreco[preco] += v;
                            else volPorPreco[preco] = v;
                        }
                    }
                    else
                    {
                        // ---- Normal: aproxima distribuindo o volume da barra pela faixa ----
                        double vol = Volume[ba];
                        if (vol <= 0) continue;
                        int niveis = (int)Math.Round((hi - lo) / tick) + 1;
                        if (niveis < 1) niveis = 1;
                        double volPorNivel = vol / niveis;   // distribuição uniforme pela faixa
                        for (int k = 0; k < niveis; k++)
                        {
                            double preco = Instrument.MasterInstrument.RoundToTickSize(lo + k * tick);
                            if (volPorPreco.ContainsKey(preco)) volPorPreco[preco] += volPorNivel;
                            else volPorPreco[preco] = volPorNivel;
                        }
                    }
                }

                if (volPorPreco.Count == 0) return 0.0;
                // POC = faixa de preço com maior volume acumulado
                double poc = 0; double maxV = -1;
                foreach (var kv in volPorPreco)
                    if (kv.Value > maxV) { maxV = kv.Value; poc = kv.Key; }
                return poc;
            }
            catch { return 0.0; }
        }

        // Verifica se o preço está PERTO de algum POC de zona ativa (dentro da tolerância).
        private bool PertoDePOC(double price)
        {
            if (!UsarConfluenciaPOC) return false;
            try
            {
                double tol = TickSize * PocToleranciaTicks;
                return ZonasEmUso.Any(z => z.a && z.poc > 0 && Math.Abs(price - z.poc) <= tol);
            }
            catch { return false; }
        }

        private bool isValidSupplyZone(double hi, double lo) { return !Zones.Any(z => z.a && z.t == "s" && ((hi <= z.h && hi >= z.l) || (lo <= z.h && lo >= z.l))); }

        // ── VOLUME PROFILE DO RANGE DE CONSOLIDAÇÃO ──
        // Estado do profile calculado sobre o último range de consolidação detectado.
        private double _vpPOC = 0, _vpVAH = 0, _vpVAL = 0;
        private bool _vpValido = false;
        private int _vpUltimoCalculo = -100;

        // Detecta se as últimas N barras formam um RANGE DE CONSOLIDAÇÃO: amplitude
        // total contida (High-Low do bloco) menor que um múltiplo do ATR — ou seja, o
        // preço está "preso" lateralmente, acumulando volume.
        private bool EmConsolidacao(int janela, out int barInicio, out int barFim)
        {
            barInicio = janela; barFim = 0;
            try
            {
                if (CurrentBar < janela + 2) return false;
                double hi = double.MinValue, lo = double.MaxValue;
                for (int i = 0; i < janela; i++) { if (High[i] > hi) hi = High[i]; if (Low[i] < lo) lo = Low[i]; }
                double amplitude = hi - lo;
                double atrRef;
                try { atrRef = atrInd[0]; } catch { atrRef = TickSize * 10; }
                if (atrRef <= 0) atrRef = TickSize * 10;
                // consolidação: amplitude do bloco < ATR * fator (ex.: 3x) → lateralização
                return amplitude <= atrRef * VpConsolidacaoFator;
            }
            catch { return false; }
        }

        // Calcula o Volume Profile (POC, VAH, VAL) de uma região de barras e devolve os
        // três níveis via out. Usado tanto no range de consolidação quanto nas zonas.
        private bool CalcularVolumeProfileEm(int barsAgoInicio, int barsAgoFim, out double poc, out double vah, out double val)
        {
            poc = 0; vah = 0; val = 0;
            try
            {
                int ini = Math.Min(barsAgoInicio, barsAgoFim);
                int fim = Math.Max(barsAgoInicio, barsAgoFim);
                double tick = TickSize <= 0 ? 0.01 : TickSize;
                var volBars = Bars.BarsSeries.BarsType as NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;

                var vp = new System.Collections.Generic.SortedDictionary<double, double>();
                for (int ba = ini; ba <= fim; ba++)
                {
                    int abs = CurrentBar - ba;
                    if (abs < 0) continue;
                    double lo = Low[ba], hi = High[ba];
                    if (hi < lo) continue;

                    if (volBars != null)
                    {
                        for (double p = lo; p <= hi + tick / 2.0; p += tick)
                        {
                            double preco = Instrument.MasterInstrument.RoundToTickSize(p);
                            long v = 0; try { v = volBars.Volumes[abs].GetTotalVolumeForPrice(preco); } catch { }
                            if (v <= 0) continue;
                            if (vp.ContainsKey(preco)) vp[preco] += v; else vp[preco] = v;
                        }
                    }
                    else
                    {
                        double vol = Volume[ba]; if (vol <= 0) continue;
                        int niveis = (int)Math.Round((hi - lo) / tick) + 1; if (niveis < 1) niveis = 1;
                        double vpn = vol / niveis;
                        for (int k = 0; k < niveis; k++)
                        {
                            double preco = Instrument.MasterInstrument.RoundToTickSize(lo + k * tick);
                            if (vp.ContainsKey(preco)) vp[preco] += vpn; else vp[preco] = vpn;
                        }
                    }
                }

                if (vp.Count == 0) return false;

                var niveisArr = vp.Keys.ToList();
                double volTotal = vp.Values.Sum();
                int idxPoc = 0; double maxV = -1;
                for (int i = 0; i < niveisArr.Count; i++)
                    if (vp[niveisArr[i]] > maxV) { maxV = vp[niveisArr[i]]; idxPoc = i; }
                poc = niveisArr[idxPoc];

                double alvo = volTotal * 0.70;
                double acum = vp[niveisArr[idxPoc]];
                int lowIdx = idxPoc, highIdx = idxPoc;
                while (acum < alvo && (lowIdx > 0 || highIdx < niveisArr.Count - 1))
                {
                    double volAbaixo = lowIdx > 0 ? vp[niveisArr[lowIdx - 1]] : -1;
                    double volAcima = highIdx < niveisArr.Count - 1 ? vp[niveisArr[highIdx + 1]] : -1;
                    if (volAcima >= volAbaixo && volAcima >= 0) { highIdx++; acum += volAcima; }
                    else if (volAbaixo >= 0) { lowIdx--; acum += volAbaixo; }
                    else break;
                }
                val = niveisArr[lowIdx];
                vah = niveisArr[highIdx];
                return true;
            }
            catch { return false; }
        }

        // Wrapper para o range de consolidação (mantém o estado _vp*).
        private void CalcularVolumeProfile(int barsAgoInicio, int barsAgoFim)
        {
            double poc, vah, val;
            _vpValido = CalcularVolumeProfileEm(barsAgoInicio, barsAgoFim, out poc, out vah, out val);
            if (_vpValido) { _vpPOC = poc; _vpVAH = vah; _vpVAL = val; }
        }

        // Volume Profile a partir de uma série Bars (usado nas zonas MTF do BarsRequest).
        // Aproxima distribuindo o volume de cada barra pela sua faixa de preço.
        private bool VolumeProfileDeBars(NinjaTrader.Data.Bars bars, int idxIni, int idxFim, out double poc, out double vah, out double val)
        {
            poc = 0; vah = 0; val = 0;
            try
            {
                double tick = TickSize <= 0 ? 0.01 : TickSize;
                int n = bars.Count;
                int ini = Math.Max(0, Math.Min(idxIni, idxFim));
                int fim = Math.Min(n - 1, Math.Max(idxIni, idxFim));
                var vp = new System.Collections.Generic.SortedDictionary<double, double>();

                for (int i = ini; i <= fim; i++)
                {
                    double lo = bars.GetLow(i), hi = bars.GetHigh(i), vol = bars.GetVolume(i);
                    if (hi < lo || vol <= 0) continue;
                    int niveis = (int)Math.Round((hi - lo) / tick) + 1; if (niveis < 1) niveis = 1;
                    double vpn = vol / niveis;
                    for (int k = 0; k < niveis; k++)
                    {
                        double preco = Instrument.MasterInstrument.RoundToTickSize(lo + k * tick);
                        if (vp.ContainsKey(preco)) vp[preco] += vpn; else vp[preco] = vpn;
                    }
                }
                if (vp.Count == 0) return false;

                var arr = vp.Keys.ToList();
                double total = vp.Values.Sum();
                int idxPoc = 0; double maxV = -1;
                for (int i = 0; i < arr.Count; i++) if (vp[arr[i]] > maxV) { maxV = vp[arr[i]]; idxPoc = i; }
                poc = arr[idxPoc];

                double alvo = total * 0.70, acum = vp[arr[idxPoc]];
                int lo2 = idxPoc, hi2 = idxPoc;
                while (acum < alvo && (lo2 > 0 || hi2 < arr.Count - 1))
                {
                    double vAb = lo2 > 0 ? vp[arr[lo2 - 1]] : -1;
                    double vAc = hi2 < arr.Count - 1 ? vp[arr[hi2 + 1]] : -1;
                    if (vAc >= vAb && vAc >= 0) { hi2++; acum += vAc; }
                    else if (vAb >= 0) { lo2--; acum += vAb; }
                    else break;
                }
                val = arr[lo2]; vah = arr[hi2];
                return true;
            }
            catch { return false; }
        }

        // Preço está numa BORDA forte do profile (perto de VAH, VAL ou POC)?
        // Verifica tanto o profile do range de consolidação quanto o das ZONAS ativas
        // (pivôs / suporte-resistência / supply & demand), se habilitado.
        private bool NaBordaValueArea(double price)
        {
            double tol = TickSize * VpToleranciaTicks;
            // 1) profile do range de consolidação
            if (_vpValido && (Math.Abs(price - _vpVAH) <= tol
                           || Math.Abs(price - _vpVAL) <= tol
                           || Math.Abs(price - _vpPOC) <= tol))
                return true;
            // 2) profile das zonas ativas (pivôs / S&R / supply & demand)
            if (VpNasZonas)
            {
                foreach (var z in ZonasEmUso)
                {
                    if (!z.a) continue;
                    if (z.poc > 0 && Math.Abs(price - z.poc) <= tol) return true;
                    if (z.vah > 0 && Math.Abs(price - z.vah) <= tol) return true;
                    if (z.val > 0 && Math.Abs(price - z.val) <= tol) return true;
                }
            }
            return false;
        }

        private bool isValidDemandZone(double hi, double lo) { return !Zones.Any(z => z.a && z.t == "d" && ((lo >= z.l && lo <= z.h) || (hi >= z.l && hi <= z.h))); }

        private int isDnContinuation()
        {
            for(int i=10; i>=2; i--)
            {
                if(isDnMove(i))
                {
                    bool val = true;
                    for(int j=i; j>=1; j--) if(!isInsideDnBar(j, i)) { val = false; break; }
                    if(val) { val = false; for(int j=i; j>=1; j--) if(Close[j] >= Open[j]) { val = true; break; } }
                    if(val && isInsideDnBreakoutBar(0, i)) return i;
                }
            }
            return -1;
        }

        private int isUpContinuation()
        {
            for(int i=10; i>=2; i--)
            {
                if(isUpMove(i))
                {
                    bool val = true;
                    for(int j=i-1; j>=1; j--) if(!isInsideUpBar(j, i)) { val = false; break; }
                    if(val) { val = false; for(int j=i; j>=1; j--) if(Close[j] <= Open[j]) { val = true; break; } }
                    if(val && isInsideUpBreakoutBar(0, i)) return i;
                }
            }
            return -1;
        }

        private bool isDnMove(int idx) { return (Close[idx] < kcInd.Lower[idx] || Close[idx+1] < kcInd.Lower[idx+1] || Close[idx+2] < kcInd.Lower[idx+2]) && ((isDnBar(idx) && isDnBar(idx+1) && isDnBar(idx+2)) || isStrongDnBar(idx)); }
        private bool isUpMove(int idx) { return (Close[idx] > kcInd.Upper[idx] || Close[idx+1] > kcInd.Upper[idx+1] || Close[idx+2] > kcInd.Upper[idx+2]) && ((isUpBar(idx) && isUpBar(idx+1) && isUpBar(idx+2)) || isStrongUpBar(idx)); }

        private bool isDnBar(int idx) { return Close[idx] < Open[idx] && Close[idx] < Close[idx+1] && High[idx] < High[idx+1] && Low[idx] < Low[idx+1]; }
        private bool isUpBar(int idx) { return Close[idx] > Open[idx] && Close[idx] > Close[idx+1] && High[idx] > High[idx+1] && Low[idx] > Low[idx+1]; }

        private bool isStrongDnBar(int idx) { return Close[idx] < Open[idx] && Close[idx] < Close[idx+1] && ((High[idx] < High[idx+1] && Low[idx] < Low[idx+1] && Low[idx] < GetMin(3, idx+1) && High[idx] - Low[idx] > atrInd[1]) || (Close[idx] < GetMin(3, idx+1) && High[idx] - Low[idx] > atrInd[1] * 2)); }
        private bool isStrongUpBar(int idx) { return Close[idx] > Open[idx] && Close[idx] > Close[idx+1] && ((High[idx] > High[idx+1] && Low[idx] > Low[idx+1] && High[idx] > GetMax(3, idx+1) && High[idx] - Low[idx] > atrInd[1]) || (Close[idx] > GetMax(3, idx+1) && High[idx] - Low[idx] > atrInd[1] * 2)); }

        private bool isInsideDnBar(int i1, int i2) { return High[i1] <= High[i2] && Math.Min(Open[i1], Close[i1]) >= Low[i2]; }
        private bool isInsideUpBar(int i1, int i2) { return Low[i1] >= Low[i2] && High[i1] <= High[i2] && Math.Max(Open[i1], Close[i1]) <= High[i2]; }

        private bool isInsideDnBreakoutBar(int i1, int i2) { return High[i1] <= High[i2] && Close[i1] <= GetMin(i2-i1, 1) && Low[i1] < GetMin(i2-i1, 1); }
        private bool isInsideUpBreakoutBar(int i1, int i2) { return Low[i1] >= Low[i2] && Close[i1] >= GetMax(i2-i1, 1) && High[i1] > GetMax(i2-i1, 1); }

        private void updateZonesSD()
        {
            foreach(Zone z in Zones)
            {
                if(z.a)
                {
                    // Bipolaridade: ao romper, a zona NÃO morre — ela é marcada como
                    // rompida (polaridade invertida) e continua ativa, estendida para
                    // frente. Quando o preço voltar, pode gerar sinal do lado oposto.
                    if(z.t == "s" && High[0] > z.h && !z.rompida) { z.rompida = true; }
                    if(z.t == "d" && Low[0] < z.l && !z.rompida) { z.rompida = true; }
                }
            }
        }

        // Lista de zonas EM USO: quando um TF de zona foi escolhido e as zonas MTF já
        // estão prontas, usa-as EXCLUSIVAMENTE; senão, usa as zonas do gráfico atual.
        private System.Collections.Generic.List<Zone> ZonasEmUso
        {
            get { return (TimeframeZonaMin > 0 && _zonasMTFProntas && _zonasMTF.Count > 0) ? _zonasMTF : Zones; }
        }

        private bool IsInActiveSupplyZone(double price) { return ZonasEmUso.Any(z => z.a && z.TipoEfetivo == "s" && price >= z.l && price <= z.h) || DipcorpTemZona(price, true); }
        private bool IsInActiveDemandZone(double price) { return ZonasEmUso.Any(z => z.a && z.TipoEfetivo == "d" && price >= z.l && price <= z.h) || DipcorpTemZona(price, false); }

        // Checagem de zona COM TOLERÂNCIA (em ticks): considera o preço "na zona" mesmo
        // um pouco antes de entrar nela. Usado no filtro de sinal para pegar o teste da
        // região (bipolaridade) sem exigir o preço exatamente dentro dos limites estreitos.
        private double ZonaTolPts { get { return TickSize * ZonaToleranciaTicks; } }
        private bool PertoDeSupply(double price)
        {
            if (TimeframeZonaMin > 0)
            {
                // Analisa da % escolhida (InicioZonaPct) até o EXTREMO (topo = falha).
                // 0% = zona inteira · 100% = só no topo.
                double frac = Math.Max(0.0, Math.Min(1.0, InicioZonaPct / 100.0));
                return ZonasEmUso.Any(z => z.a && z.TipoEfetivo == "s" &&
                    price >= z.l + (z.h - z.l) * frac && price <= z.h + ZonaTolPts);
            }
            if (ZonasEmUso.Any(z => z.a && z.TipoEfetivo == "s" && price >= z.l - ZonaTolPts && price <= z.h + ZonaTolPts)) return true;
            return DipcorpTemZona(price, true);
        }
        private bool PertoDeDemand(double price)
        {
            if (TimeframeZonaMin > 0)
            {
                // Analisa da % escolhida até o EXTREMO (fundo = falha).
                double frac = Math.Max(0.0, Math.Min(1.0, InicioZonaPct / 100.0));
                return ZonasEmUso.Any(z => z.a && z.TipoEfetivo == "d" &&
                    price <= z.h - (z.h - z.l) * frac && price >= z.l - ZonaTolPts);
            }
            if (ZonasEmUso.Any(z => z.a && z.TipoEfetivo == "d" && price >= z.l - ZonaTolPts && price <= z.h + ZonaTolPts)) return true;
            return DipcorpTemZona(price, false);
        }

        // ── EXTREMOS DA ZONA ──
        // Venda só no TOPO da supply (últimos X% perto de z.h).
        // Compra só no FUNDO da demand (primeiros X% perto de z.l).
        // Nunca no meio da zona.
        private bool NoExtremoSupply(double price)
        {
            double frac = Math.Max(0.05, Math.Min(0.5, ExtremoZonaPct / 100.0));
            // zonas internas (usa tipo efetivo p/ bipolaridade)
            foreach (var z in Zones)
            {
                if (!z.a || z.TipoEfetivo != "s") continue;
                double alt = z.h - z.l;
                if (alt <= 0) continue;
                double limiteExtremo = z.h - alt * frac;   // faixa superior
                if (price >= limiteExtremo && price <= z.h + DipcorpTolPts) return true;
            }
            // zonas Dipcorp (2min)
            return DipcorpNoExtremo(price, true, frac);
        }

        private bool NoExtremoDemand(double price)
        {
            double frac = Math.Max(0.05, Math.Min(0.5, ExtremoZonaPct / 100.0));
            foreach (var z in Zones)
            {
                if (!z.a || z.TipoEfetivo != "d") continue;
                double alt = z.h - z.l;
                if (alt <= 0) continue;
                double limiteExtremo = z.l + alt * frac;   // faixa inferior
                if (price <= limiteExtremo && price >= z.l - DipcorpTolPts) return true;
            }
            return DipcorpNoExtremo(price, false, frac);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // SINAL 1.0 — TENDÊNCIA / BIPOLARIDADE (construído do zero)
        // ---------------------------------------------------------------------------
        // Confluência com pesos (não precisa 100%). Blocos:
        //  (A) REGIÃO de pivô: preço tocou a região inteira (ambos extremos) de uma
        //      zona bipolar — supply (venda) ou demand (compra).
        //  (B) EMA 9 do gráfico atual: preço do lado coerente com a direção.
        //  (C) ESTOCÁSTICO nos extremos (≥80 / ≤20) + cruzamento K×D na direção.
        //  (D) DELTA/AGRESSÃO: divergência clássica (preço x delta) E o saldo de
        //      agressão do lado que chegou forte enfraquecendo dentro da zona,
        //      com o lado oposto assumindo (medido tick a tick).
        // Retorna score 0..100 e a direção (isVenda). Emite se score >= limiar.
        // ═══════════════════════════════════════════════════════════════════════

        // Pesos de cada bloco (somam 100).
        private const int PESO_REGIAO = 25;   // extremo da zona
        private const int PESO_EMA    = 10;   // tendência EMA9
        private const int PESO_STOCH  = 20;   // cruzamento estocástico
        private const int PESO_DELTA  = 25;   // divergência delta/agressão
        private const int PESO_VOLUME = 10;   // volume diminuindo no pullback
        private const int PESO_CANDLE = 10;   // candle de rejeição

        // Atualiza o rastreador de agressão conforme o preço entra/sai da região.
        // Chamado 1x por barra (usa o delta real acumulado da barra).
        private void AtualizarRastreadorZona(bool insideSupply, bool insideDemand)
        {
            bool dentro = insideSupply || insideDemand;
            if (dentro && !_naZona)
            {
                // acabou de ENTRAR na zona → zera o rastreador
                _naZona = true;
                _zonaVenda = insideSupply;
                _aggPicoComprador = Math.Max(0, barDeltaReal);
                _aggPicoVendedor = Math.Min(0, barDeltaReal);
                _deltaEntradaZona = deltaSeries[0];
                _barsNaZona = 0;
            }
            else if (dentro)
            {
                // continua DENTRO → atualiza picos de agressão
                _barsNaZona++;
                if (barDeltaReal > _aggPicoComprador) _aggPicoComprador = barDeltaReal;
                if (barDeltaReal < _aggPicoVendedor)  _aggPicoVendedor = barDeltaReal;
            }
            else
            {
                // SAIU da zona
                _naZona = false;
                _barsNaZona = 0;
            }
        }

        // Divergência de agressão dentro da zona.
        // Venda: chegou comprador forte (pico comprador alto), mas agora o delta da
        //        barra virou negativo (vendedor assumindo) = comprador enfraqueceu.
        // Compra: espelho.
        private bool DivergenciaAgressaoNaZona(bool isVenda)
        {
            if (!_naZona) return false;
            if (isVenda)
            {
                // chegou comprador forte na zona e agora enfraqueceu (delta bem abaixo do pico)
                // e a barra fechou vendedora → vendedor assumindo
                bool chegouCompradorForte = _aggPicoComprador > 0;
                bool compradorEnfraqueceu = barDeltaReal < _aggPicoComprador * 0.4;
                return chegouCompradorForte && compradorEnfraqueceu && barDeltaReal < 0;
            }
            else
            {
                bool chegouVendedorForte = _aggPicoVendedor < 0;
                bool vendedorEnfraqueceu = barDeltaReal > _aggPicoVendedor * 0.4; // menos negativo
                return chegouVendedorForte && vendedorEnfraqueceu && barDeltaReal > 0;
            }
        }

        // Divergência clássica preço × delta (na direção do sinal).
        private bool DivergenciaClassica(bool isVenda)
        {
            try
            {
                if (isVenda)
                    // preço fez topo mais alto, delta fez topo mais baixo (força vendida)
                    return High[0] > High[1] && deltaSeries[0] < deltaSeries[1];
                else
                    return Low[0] < Low[1] && deltaSeries[0] > deltaSeries[1];
            }
            catch { return false; }
        }

        // ── ESTOCÁSTICO: só a média K nos extremos (níveis configuráveis) ──
        // Retorna +1 (K sobrevendido → compra), -1 (K sobrecomprado → venda), 0.
        private int EstocasticoExtremoK()
        {
            try
            {
                var st = _estado.Estoc_585 ? stoch585 : stoch353;
                if (st == null) return 0;
                double k = st.K[0];
                if (double.IsNaN(k)) return 0;
                double ob = StochOverboughtLevel > 0 ? StochOverboughtLevel : 80;
                double os = StochOversoldLevel > 0 ? StochOversoldLevel : 20;
                if (k <= os) return 1;    // sobrevendido → compra
                if (k >= ob) return -1;   // sobrecomprado → venda
            }
            catch { }
            return 0;
        }

        // Grau (0..1) de quão extremo está o K, para o cálculo contínuo (gradiente).
        private double EstocasticoGrau(bool isVenda)
        {
            try
            {
                var st = _estado.Estoc_585 ? stoch585 : stoch353;
                if (st == null) return 0;
                double k = st.K[0];
                if (double.IsNaN(k)) return 0;
                // venda: quanto mais alto o K (perto de 100), mais extremo; compra: quanto mais baixo
                double v = isVenda ? (k - 50.0) / 50.0 : (50.0 - k) / 50.0;
                return Math.Max(0, Math.Min(1, v));
            }
            catch { return 0; }
        }

        // Cruzamento da linha K com a D, a favor da direção:
        //   compra → K cruza D para CIMA · venda → K cruza D para BAIXO.
        // Usado como gatilho opcional do Sinal 2.0 (repique na média).
        private bool CruzamentoKxD(bool isVenda)
        {
            try
            {
                var st = _estado.Estoc_585 ? stoch585 : stoch353;
                if (st == null) return false;
                double k0 = st.K[0], d0 = st.D[0];
                double k1 = st.K[1], d1 = st.D[1];
                if (double.IsNaN(k0) || double.IsNaN(d0) || double.IsNaN(k1) || double.IsNaN(d1)) return false;
                bool cruzouCima = k1 <= d1 && k0 > d0;   // K passou por cima da D
                bool cruzouBaixo = k1 >= d1 && k0 < d0;  // K passou por baixo da D
                return isVenda ? cruzouBaixo : cruzouCima;
            }
            catch { return false; }
        }

        // Volume diminuindo no pullback (pullback "sem força").
        private bool VolumeDiminuindoPullback()
        {
            try
            {
                if (volumeMediaInd == null) return false;
                double media = volumeMediaInd[0];
                return media > 0 && Volume[0] < media * 0.85;
            }
            catch { return false; }
        }

        // Candle de rejeição na direção (pavio longo contra + corpo pequeno).
        private bool CandleRejeicao(bool isVenda)
        {
            try
            {
                double corpo = Math.Abs(Close[0] - Open[0]);
                double range = High[0] - Low[0];
                if (range <= 0) return false;
                double pavioSup = High[0] - Math.Max(Close[0], Open[0]);
                double pavioInf = Math.Min(Close[0], Open[0]) - Low[0];
                bool corpoPequeno = corpo < range * 0.5;
                if (isVenda) return corpoPequeno && pavioSup > range * 0.4;
                else         return corpoPequeno && pavioInf > range * 0.4;
            }
            catch { return false; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ESTRATÉGIA PADRÃO — bipolaridade/pullback nos extremos dos pivôs.
        // 3 PILARES com pesos configuráveis no painel:
        //  1. ESTRUTURA  = price action (pivô Dow) + ZigZag + extremo de zona S&D
        //  2. ESTOCÁSTICO = média K na extremidade 80/20 (3/5/3 ou 5/8/5)
        //  3. FLUXO      = saldo de agressão / delta / times&trades a favor
        // Emite quando o score ponderado ≥ Padrao_ScoreMin.
        // ═══════════════════════════════════════════════════════════════════════

        // (1) ESTRUTURA: o preço está no extremo de um pivô/zona? Retorna 0..1.
        // Combina: pivô recente de price action (Dow) + extremo de zona S&D.
        private double AvaliarEstrutura(bool isVenda, out bool noExtremoZona)
        {
            noExtremoZona = false;
            double s = 0;
            try
            {
                // extremo de zona S&D (topo supply / fundo demand) — peso maior
                noExtremoZona = isVenda
                    ? (NoExtremoSupply(High[0]) || NoExtremoSupply(Close[0]))
                    : (NoExtremoDemand(Low[0]) || NoExtremoDemand(Close[0]));
                if (noExtremoZona) s += 0.6;

                // pivô de price action / ZigZag (Dow): topo recente p/ venda, fundo p/ compra
                bool pivoOk = isVenda ? PivoTopoRecente() : PivoFundoRecente();
                if (pivoOk) s += 0.4;
            }
            catch { }
            return Math.Max(0, Math.Min(1, s));
        }

        // Pivô de topo recente (price action estilo Dow/ZigZag): topo confirmado nas últimas barras.
        private bool PivoTopoRecente()
        {
            try
            {
                int str = Math.Max(2, SwingStrength);
                // um topo em [str] barras atrás, mais alto que os vizinhos
                for (int i = str; i <= str + 4 && i + str < CurrentBar; i++)
                {
                    bool topo = true;
                    for (int j = 1; j <= str; j++)
                        if (High[i] < High[i - j] || High[i] < High[i + j]) { topo = false; break; }
                    if (topo && High[0] >= High[i] - 3 * TickSize) return true; // preço voltou ao topo
                }
            }
            catch { }
            return false;
        }

        private bool PivoFundoRecente()
        {
            try
            {
                int str = Math.Max(2, SwingStrength);
                for (int i = str; i <= str + 4 && i + str < CurrentBar; i++)
                {
                    bool fundo = true;
                    for (int j = 1; j <= str; j++)
                        if (Low[i] > Low[i - j] || Low[i] > Low[i + j]) { fundo = false; break; }
                    if (fundo && Low[0] <= Low[i] + 3 * TickSize) return true;
                }
            }
            catch { }
            return false;
        }

        // (3) FLUXO: saldo de agressão favorável ao movimento. Retorna 0..1.
        // Combina delta real da barra + divergência de agressão na zona.
        private double AvaliarFluxo(bool isVenda)
        {
            double f = 0;
            try
            {
                // saldo de agressão da barra a favor (venda = delta negativo; compra = positivo)
                double delta = deltaSeries[0];
                bool aFavor = isVenda ? delta < 0 : delta > 0;
                if (aFavor) f += 0.4;

                // divergência de agressão na zona (player que chegou forte enfraquecendo)
                if (DivergenciaAgressaoNaZona(isVenda)) f += 0.4;

                // divergência clássica preço×delta
                if (DivergenciaClassica(isVenda)) f += 0.2;
            }
            catch { }
            return Math.Max(0, Math.Min(1, f));
        }

        // Bloqueia sinais em horários de baixa liquidez / alta manipulação:
        // 00:00–04:30 (madrugada) e 17:00–18:00 (ajuste/rollover). Usa o horário
        // do candle (Time[0]). Retorna true = horário bloqueado (não emitir).
        private bool HorarioBloqueado()
        {
            if (!FiltrarHorario) return false;
            try
            {
                var t = Time[0].TimeOfDay;
                var h00 = new TimeSpan(0, 0, 0);
                var h0430 = new TimeSpan(4, 30, 0);
                var h17 = new TimeSpan(17, 0, 0);
                var h18 = new TimeSpan(18, 0, 0);
                if (t >= h00 && t < h0430) return true;   // 00:00–04:30
                if (t >= h17 && t < h18) return true;     // 17:00–18:00
            }
            catch { }
            return false;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // MOTOR INSTITUCIONAL — hierarquia: REGIÃO → FLUXO → FLIP → CONFIRMAÇÕES.
        // Prioriza qualidade sobre quantidade. Sem região de liquidez, não opera.
        // ═══════════════════════════════════════════════════════════════════════
        private bool AvaliarSinalPadrao(out int score, out bool isVenda, out string confluencias)
        {
            score = 0; isVenda = false; confluencias = "";
            if (HorarioBloqueado()) return false;   // horário de baixa liquidez

            // ── PLUS DIVERGÊNCIA (via própria) ──
            if (_estado.PlusDivergencia)
            {
                if (PlusDivergencia(false)) { isVenda = false; score = 90; confluencias = "PLUS Divergência (armadilha demand)"; return true; }
                if (PlusDivergencia(true))  { isVenda = true;  score = 90; confluencias = "PLUS Divergência (armadilha supply)"; return true; }
            }

            var conf = new System.Collections.Generic.List<string>();

            // Direção candidata pela posição/estocástico (ou pela região testada)
            int estoc = EstocasticoExtremoK();
            bool temDirEstoc = estoc != 0;
            isVenda = temDirEstoc ? (estoc == -1) : (ProbBias() == "VENDA");

            // ═══════════════════════════════════════════════════════════════════
            // REGRA FIXA DO SINAL 1.0 (sem pesos/score configurável):
            //   1) BASE — estar numa REGIÃO de liquidez (S/R · supply/demand)  [OBRIGATÓRIO]
            //   2) FLUXO — delta de agressão (times&trades) A FAVOR             [OBRIGATÓRIO]
            //   3) CONFLUÊNCIA — pelo menos 1 entre Estocástico / BOP / IFR     [≥1 OBRIGATÓRIA]
            //      (por confluência OU divergência a favor da região)
            // ═══════════════════════════════════════════════════════════════════

            // ── 1) REGIÃO DE LIQUIDEZ (obrigatório) ──
            bool dentroZona = isVenda
                ? (PertoDeSupply(High[0]) || PertoDeSupply(Close[0]))
                : (PertoDeDemand(Low[0]) || PertoDeDemand(Close[0]));
            if (!dentroZona) return false;
            conf.Add(isVenda ? "Região VENDA (supply/resistência)" : "Região COMPRA (demand/suporte)");

            // ── 2) DELTA DE AGRESSÃO A FAVOR (obrigatório) ──
            // venda → delta vendedor (≤0) · compra → delta comprador (≥0).
            // No histórico usa o delta da barra; em tempo real usa o fluxo real.
            double delta = deltaSeries[0];
            bool deltaAFavor = isVenda ? delta <= 0 : delta >= 0;
            if (!deltaAFavor) return false;
            conf.Add("Delta a favor");

            // ── 3) CONFLUÊNCIA — ao menos 1 entre Estocástico / BOP / IFR ──
            // Conta tanto CONFLUÊNCIA (indicador a favor) quanto DIVERGÊNCIA (preço
            // renova extremo e indicador não acompanha), na direção da região.
            int nConfl = 0;

            // Estocástico (3/5/3 por padrão, K=5): no extremo a favor OU divergência
            bool estocOK = temDirEstoc || DivergenciaStoch(isVenda);
            if (estocOK) { nConfl++; conf.Add("Estocástico"); }

            // BOP (suavizado): força na direção OU divergência de BOP
            bool bopOK = BopAFavor(isVenda) || DivergenciaBOP(isVenda);
            if (bopOK) { nConfl++; conf.Add("BOP"); }

            // IFR / RSI: divergência de RSI (preço renova extremo, RSI não acompanha)
            bool ifrOK = DivergenciaRSI(isVenda);
            if (ifrOK) { nConfl++; conf.Add("IFR/RSI"); }

            // DELTA (fluxo de agressão) — reforço quando o delta CONFIRMA a reversão:
            // divergência (preço renova extremo mas o delta não acompanha) ou exaustão
            // (pico de agressão seguido de queda abrupta na direção contrária). No NQ,
            // esse é um dos sinais de reversão mais confiáveis.
            if (ReforcoDelta && (DivergenciaDelta(isVenda) || ExaustaoDelta(isVenda)))
            { nConfl++; conf.Add("Delta (divergência/exaustão)"); }

            // POC (perfil de volume) — sinal perto do Point of Control de um pivô ganha
            // força: é um nível institucional onde muito volume foi negociado.
            if (UsarConfluenciaPOC && (PertoDePOC(Close[0]) || PertoDePOC(isVenda ? High[0] : Low[0])))
            { nConfl++; conf.Add("POC (perfil de volume)"); }

            if (nConfl < 1) return false;   // precisa de pelo menos 1 confluência

            // score informativo (não configurável): base 60 + 10 por confluência extra
            score = 60 + Math.Min(3, nConfl) * 10;
            confluencias = string.Join(" · ", conf);
            return true;
        }

        // BOP suavizado a favor da direção (venda: BOP<0 · compra: BOP>0).
        private bool BopAFavor(bool isVenda)
        {
            try
            {
                if (bopSmoothed == null) return false;
                double b = bopSmoothed[0];
                return isVenda ? b < 0 : b > 0;
            }
            catch { return false; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // NÚCLEO DO SINAL 2.0 — ESTRUTURA DE TENDÊNCIA POR EMA 9 / 30
        // Lê estrutura, inclinação (slope), separação (expansão/compressão),
        // curvatura, pullback e rompimento de pivô — NÃO usa cruzamento (atrasado).
        // Trata a EMA 9 como vetor de MOMENTUM e a EMA 30 como vetor de TENDÊNCIA.
        // Retorna um SCORE de força (0-100) e a direção; score 0 = sem tendência.
        // ═══════════════════════════════════════════════════════════════════════
        private int AvaliarEstruturaEMA20(out bool isVenda, out string detalhe)
        {
            isVenda = false; detalhe = "";
            try
            {
                if (ema9 == null || ema30 == null) return 0;
                if (CurrentBar < 6) return 0;

                double tick = TickSize <= 0 ? 0.01 : TickSize;
                double e9 = ema9[0], e30 = ema30[0];
                double e9_5 = ema9[5], e30_5 = ema30[5];
                double e9_1 = ema9[1], e9_2 = ema9[2];

                // ── ESTÁGIO 1 — Definição da tendência (filtro mestre) ──
                bool alta = e9 > e30;
                isVenda = !alta;

                // ── ESTÁGIO 2 — Slope (inclinação) das duas médias ──
                double slope9 = (e9 - e9_5) / tick;    // em ticks nas últimas 5 barras
                double slope30 = (e30 - e30_5) / tick;
                double slopeMin = Math.Max(0.5, _estado.Sinal20_SlopeMin * 0.1);

                // inclinações a favor da direção da tendência?
                bool slope9OK = alta ? slope9 > slopeMin : slope9 < -slopeMin;
                bool slope30OK = alta ? slope30 > slopeMin : slope30 < -slopeMin;

                // filtro mestre: sem as duas médias inclinadas a favor → SEM tendência
                if (!slope9OK || !slope30OK) return 0;

                // ── ESTÁGIO 3 e 4 — Distância entre médias e sua direção ──
                double distNow = Math.Abs(e9 - e30) / tick;
                double distAnt = Math.Abs(e9_1 - ema30[1]) / tick;
                bool expandindo = distNow > distAnt;         // tendência acelerando
                bool comprimindo = distNow < distAnt;        // pullback/perda de força
                // ESTÁGIO 8 (cancelamento): EMA 9 indo em direção à EMA 30 = perda de força
                if (comprimindo && distNow < _estado.Sinal20_CompressaoMin) return 0;

                // ── ESTÁGIO 5 — Curvatura (slope atual vs anterior da EMA 9) ──
                double slope9_curto = (e9 - e9_1) / tick;
                double slope9_ant = (e9_1 - e9_2) / tick;
                bool acelerando = alta ? slope9_curto > slope9_ant : slope9_curto < slope9_ant;

                // ── ESTÁGIO 6 — Pullback: preço voltou perto da EMA 9 ──
                bool pullback = AncoradoEmEMA(isVenda);

                // ── ESTÁGIO 7 — Rompimento do último pivô ──
                bool rompeuPivo = false;
                try
                {
                    if (alta && lastHighPivot != null && lastHighPivot.PriceHigh > 0)
                        rompeuPivo = Close[0] > lastHighPivot.PriceHigh;
                    else if (!alta && lastLowPivot != null && lastLowPivot.PriceLow > 0)
                        rompeuPivo = Close[0] < lastLowPivot.PriceLow;
                }
                catch { }

                // ── ESTÁGIO 9 — Pontuação de força (0-100) ──
                int score = 0;
                var d = new System.Collections.Generic.List<string>();
                score += 20; d.Add(alta ? "EMA9>EMA30" : "EMA9<EMA30");   // posição
                if (slope9OK)  { score += 20; d.Add("EMA9 inclinada"); }
                if (slope30OK) { score += 20; d.Add("EMA30 inclinada"); }
                if (expandindo){ score += 15; d.Add("Distância↑"); }
                if (acelerando){ score += 10; d.Add("Acelerando"); }
                if (pullback)  { score += 10; d.Add("Pullback"); }
                if (rompeuPivo){ score += 5;  d.Add("Rompeu pivô"); }

                detalhe = string.Join(" · ", d);
                return Math.Max(0, Math.Min(100, score));
            }
            catch { isVenda = false; detalhe = ""; return 0; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // SINAL 2.0 — TENDÊNCIA POR ESTRUTURA DE EMA (núcleo) + gatilhos
        // O núcleo de estrutura (EMA 9/30) tem PRIORIDADE ABSOLUTA. Só depois de
        // confirmar a tendência é que fluxo/gatilhos entram para refinar.
        // Hierarquia: score ≥ 85 → SETA (forte) · 65-84 → BOLINHA (fraca) · <65 nada.
        // ═══════════════════════════════════════════════════════════════════════
        private bool AvaliarSinal20Padrao(out int score, out bool isVenda, out string confluencias)
        {
            score = 0; isVenda = false; confluencias = "";
            if (HorarioBloqueado()) return false;   // horário de baixa liquidez

            // ── PLUS DIVERGÊNCIA (via própria) ──
            // Renovação de extremo com divergência RSI + delta gera sinal por conta
            // própria, além da estrutura de tendência EMA.
            if (_estado.PlusDivergencia)
            {
                if (PlusDivergencia(false)) { isVenda = false; score = 90; confluencias = "PLUS Divergência (fundo+RSI↑+delta)"; return true; }
                if (PlusDivergencia(true))  { isVenda = true;  score = 90; confluencias = "PLUS Divergência (topo+RSI↓+delta)"; return true; }
            }

            // ── NÚCLEO (prioridade absoluta): estrutura de tendência por EMA ──
            string detEstrut;
            int scoreEstrut = AvaliarEstruturaEMA20(out isVenda, out detEstrut);
            if (scoreEstrut < _estado.Sinal20_ScoreMin)
                return false;   // sem estrutura de tendência suficiente = sem sinal

            score = scoreEstrut;
            confluencias = detEstrut;

            // ── GATILHOS opcionais (refinam, somam score) — flip / candle / K×D ──
            int flip = DetectarFlip();
            bool flipOk = _estado.Sinal20_UsarFlip && ((isVenda && flip == -1) || (!isVenda && flip == 1));
            bool candleOk = _estado.Sinal20_UsarCandle && CandleRejeicao(isVenda);
            bool cruzaOk = _estado.Sinal20_UsarCruzamentoKD && CruzamentoKxD(isVenda);
            if (flipOk || candleOk || cruzaOk)
            {
                score = Math.Min(100, score + (flipOk ? 4 : 0) + (candleOk ? 3 : 0) + (cruzaOk ? 4 : 0));
                confluencias += (flipOk ? " · Flip" : "") + (candleOk ? " · Gatilho" : "") + (cruzaOk ? " · K×D" : "");
            }
            return true;
        }

        // Verifica se o preço está ANCORADO na EMA 9 ou 30 — ou seja, fez um pullback
        // que testou a média e agora está reagindo a favor. Tolerância em ticks.
        private bool AncoradoEmEMA(bool isVenda)
        {
            try
            {
                if (ema9 == null || ema30 == null) return false;
                int tolTicks = _estado.Sinal20_AncoragemTol > 0 ? _estado.Sinal20_AncoragemTol : EmaAncoragemTol;
                int barsJanela = _estado.Sinal20_AncoragemBarras > 0 ? _estado.Sinal20_AncoragemBarras : EmaAncoragemBarras;
                double tol = TickSize * Math.Max(2, tolTicks);   // zona de toque
                // olha as últimas N barras: alguma tocou a EMA 9 ou 30?
                int janela = Math.Max(1, barsJanela);
                for (int i = 0; i <= janela && i <= CurrentBar; i++)
                {
                    double e9 = ema9[i], e30 = ema30[i];
                    double hi = High[i], lo = Low[i];
                    // "tocar" = o range da barra cruzou/encostou na média (dentro da tolerância)
                    bool tocou9 = (lo - tol) <= e9 && e9 <= (hi + tol);
                    bool tocou30 = (lo - tol) <= e30 && e30 <= (hi + tol);
                    if (tocou9 || tocou30)
                    {
                        // ancoragem coerente com a direção:
                        // compra → repique de baixo p/ cima na média (preço reagindo acima)
                        // venda → repique de cima p/ baixo (preço reagindo abaixo)
                        double refEma = tocou9 ? e9 : e30;
                        if (!isVenda && Close[0] >= refEma) return true;
                        if (isVenda && Close[0] <= refEma) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        // ── TOP 2: fluxo de agressão coerente com a direção da região ──
        // Venda: compra perdendo força / venda assumindo. Compra: espelho.
        private bool FluxoCoerenteRegiao(bool isVenda)
        {
            try
            {
                // no histórico (sem tick real) fail-open para permitir backtest
                bool live = State == State.Realtime && !double.IsNaN(currentBid);
                if (!live)
                {
                    // usa o delta sintético/da barra como aproximação
                    double d = deltaSeries[0];
                    return isVenda ? d <= 0 : d >= 0;
                }
                if (isVenda)
                {
                    // comprador tentou e perdeu força: houve pico comprador e agora vira vendedor
                    bool tentouComprar = _aggPicoComprador > 0;
                    bool agoraVende = barDeltaReal < _aggPicoComprador * 0.5;
                    return tentouComprar && agoraVende;
                }
                else
                {
                    bool tentouVender = _aggPicoVendedor < 0;
                    bool agoraCompra = barDeltaReal > _aggPicoVendedor * 0.5;
                    return tentouVender && agoraCompra;
                }
            }
            catch { return true; }   // fail-open
        }

        // ── Divergências por indicador (preço renova extremo, indicador não) ──
        private bool DivergenciaBOP(bool isVenda)
        {
            try
            {
                if (bopSmoothed == null) return false;
                double b0 = bopSmoothed[0], b1 = bopSmoothed[1];
                if (isVenda) return High[0] > High[1] && b0 < b1;   // preço sobe, BOP cai
                else         return Low[0] < Low[1] && b0 > b1;     // preço cai, BOP sobe
            }
            catch { return false; }
        }

        // ── DIVERGÊNCIA DE DELTA ──
        // Preço renova o extremo mas o DELTA (agressão) não acompanha: menos convicção
        // no movimento → sinal de reversão. Venda: preço faz nova máxima mas o delta
        // comprador enfraquece. Compra: preço faz nova mínima mas o delta vendedor enfraquece.
        private bool DivergenciaDelta(bool isVenda)
        {
            try
            {
                if (deltaSeries == null || CurrentBar < 2) return false;
                double d0 = deltaSeries[0], d1 = deltaSeries[1];
                if (isVenda) return High[0] > High[1] && d0 < d1;   // topo mais alto, delta menor
                else         return Low[0] < Low[1] && d0 > d1;     // fundo mais baixo, delta maior (menos venda)
            }
            catch { return false; }
        }

        // ── EXAUSTÃO DE DELTA ──
        // Pico de agressão numa direção seguido de queda abrupta = o movimento "gastou"
        // a força. Na zona, costuma anteceder reversão. Venda: houve forte delta comprador
        // que agora despenca. Compra: forte delta vendedor que agora despenca.
        private bool ExaustaoDelta(bool isVenda)
        {
            try
            {
                if (deltaSeries == null || CurrentBar < 3) return false;
                double d0 = deltaSeries[0], d1 = deltaSeries[1], d2 = deltaSeries[2];
                if (isVenda)
                    // pico comprador (d2/d1 fortes e positivos) e agora colapsa
                    return d1 > 0 && d2 > 0 && d1 >= d2 && d0 < d1 * 0.5;
                else
                    // pico vendedor (d2/d1 fortes e negativos) e agora colapsa
                    return d1 < 0 && d2 < 0 && d1 <= d2 && d0 > d1 * 0.5;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════════════
        // PLUS DIVERGÊNCIA — ARMADILHA DE LIQUIDEZ NA ZONA (stop hunt).
        // A ideia: o preço ROMPE a zona (varre liquidez além dela) mas o fluxo e o
        // momentum já reverteram — sinal a favor da reversão para dentro da zona.
        //
        // VENDA: preço renova MÁXIMA rompendo o TOPO de uma zona de SUPPLY, mas o
        //   RSI/IFR CAI (divergência de baixa) + delta VENDEDOR → a máxima foi só
        //   armadilha p/ pegar stops de vendido; entra vendido.
        // COMPRA: preço renova MÍNIMA rompendo o FUNDO de uma zona de DEMAND, mas o
        //   RSI/IFR SOBE (divergência de alta) + delta COMPRADOR → armadilha de baixa.
        // ══════════════════════════════════════════════════════════════════════
        private bool PlusDivergencia(bool isVenda)
        {
            try
            {
                if (rsiIndicator == null) return false;
                int janela = 20;   // procura o extremo anterior nas últimas ~20 barras
                if (CurrentBar < janela + 2) return false;

                // ── TIMING: cruzamento do estocástico K×D (config 3/5/3, K=5) ──
                // compra: K cruza D para CIMA · venda: K cruza D para BAIXO.
                bool cruzouKD = CruzamentoKxD353(isVenda);

                // ── CORRELAÇÃO: BOP suavizado em 12 períodos a favor do movimento ──
                double bop12 = BopSmooth12();
                bool bopAFavor = isVenda ? bop12 < 0 : bop12 > 0;

                if (!isVenda)
                {
                    // ── COMPRA: armadilha no FUNDO de uma zona de DEMAND ──
                    int idxFundo = -1; double menor = double.MaxValue;
                    for (int i = 3; i <= janela; i++)
                        if (Low[i] < menor) { menor = Low[i]; idxFundo = i; }
                    if (idxFundo < 0) return false;

                    bool renovouFundo = Low[0] < menor;                       // renovou a mínima
                    bool rsiSubindo   = rsiIndicator[0] > rsiIndicator[idxFundo]; // divergência de alta
                    bool deltaComprador = deltaSeries[0] > 0;                 // fluxo já comprador
                    bool rompeuDemand = RompeuZonaLiquidez(false);            // varreu o fundo da zona
                    return renovouFundo && rsiSubindo && deltaComprador && rompeuDemand
                           && cruzouKD && bopAFavor;
                }
                else
                {
                    // ── VENDA: armadilha no TOPO de uma zona de SUPPLY ──
                    int idxTopo = -1; double maior = double.MinValue;
                    for (int i = 3; i <= janela; i++)
                        if (High[i] > maior) { maior = High[i]; idxTopo = i; }
                    if (idxTopo < 0) return false;

                    bool renovouTopo = High[0] > maior;                        // renovou a máxima
                    bool rsiCaindo   = rsiIndicator[0] < rsiIndicator[idxTopo];// divergência de baixa
                    bool deltaVendedor = deltaSeries[0] < 0;                   // fluxo já vendedor
                    bool rompeuSupply = RompeuZonaLiquidez(true);              // varreu o topo da zona
                    return renovouTopo && rsiCaindo && deltaVendedor && rompeuSupply
                           && cruzouKD && bopAFavor;
                }
            }
            catch { return false; }
        }

        // Cruzamento K×D fixado no estocástico 3/5/3 (K = período 5), como o timing pede.
        // compra: K cruza D para cima · venda: K cruza D para baixo.
        private bool CruzamentoKxD353(bool isVenda)
        {
            try
            {
                if (stoch353 == null) return false;
                double k0 = stoch353.K[0], d0 = stoch353.D[0];
                double k1 = stoch353.K[1], d1 = stoch353.D[1];
                if (double.IsNaN(k0) || double.IsNaN(d0) || double.IsNaN(k1) || double.IsNaN(d1)) return false;
                bool cruzouCima  = k1 <= d1 && k0 > d0;   // K passou por cima da D
                bool cruzouBaixo = k1 >= d1 && k0 < d0;   // K passou por baixo da D
                return isVenda ? cruzouBaixo : cruzouCima;
            }
            catch { return false; }
        }

        // BOP (Balance of Power) suavizado em 12 períodos — média simples da série de BOP.
        // Usado na correlação do PLUS DIVERGÊNCIA (independente do BopSmoothingPeriod geral).
        private double BopSmooth12()
        {
            try
            {
                if (bopSeries == null) return 0;
                int n = Math.Min(CurrentBar + 1, 12);
                if (n <= 0) return 0;
                double sum = 0;
                for (int i = 0; i < n; i++) sum += bopSeries[i];
                return sum / n;
            }
            catch { return 0; }
        }

        // Detecta se o candle atual ROMPEU/VARREU uma zona de liquidez para caçar stops:
        // supply → a máxima ultrapassou o topo de uma zona de supply ativa, mas fechou
        //          de volta abaixo do topo (pavio de rejeição = armadilha).
        // demand → a mínima furou o fundo de uma zona de demand ativa, mas fechou de
        //          volta acima do fundo.
        // Verifica se o preço atual está DENTRO de alguma zona de liquidez ativa
        // (supply, demand ou bipolaridade). Usado pelo filtro "Somente nas zonas".
        private bool PrecoEmZonaLiquidez(bool isVenda)
        {
            try
            {
                // Usa a MESMA checagem tolerante do motor de sinais (PertoDeSupply/Demand),
                // que já considera a tolerância em ticks — assim o filtro não fica mais
                // restritivo que a própria regra de zona obrigatória.
                if (isVenda)
                    return PertoDeSupply(High[0]) || PertoDeSupply(Close[0]);
                else
                    return PertoDeDemand(Low[0]) || PertoDeDemand(Close[0]);
            }
            catch { return false; }
        }

        private bool RompeuZonaLiquidez(bool supply)
        {
            try
            {
                foreach (var z in Zones)
                {
                    if (!z.a) continue;
                    string tipo = z.TipoEfetivo;
                    if (supply && tipo == "s")
                    {
                        // varreu o topo da supply e reagiu (fechou abaixo do topo)
                        if (High[0] > z.h && Close[0] < z.h) return true;
                    }
                    else if (!supply && tipo == "d")
                    {
                        // furou o fundo da demand e reagiu (fechou acima do fundo)
                        if (Low[0] < z.l && Close[0] > z.l) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        private bool DivergenciaRSI(bool isVenda)
        {
            try
            {
                if (rsiIndicator == null) return false;
                double r0 = rsiIndicator[0], r1 = rsiIndicator[1];
                if (isVenda) return High[0] > High[1] && r0 < r1;
                else         return Low[0] < Low[1] && r0 > r1;
            }
            catch { return false; }
        }
        private bool DivergenciaStoch(bool isVenda)
        {
            try
            {
                var st = _estado.Estoc_585 ? stoch585 : stoch353;
                if (st == null) return false;
                double k0 = st.K[0], k1 = st.K[1];
                if (isVenda) return High[0] > High[1] && k0 < k1;
                else         return Low[0] < Low[1] && k0 > k1;
            }
            catch { return false; }
        }
        private bool DivergenciaVolume(bool isVenda)
        {
            try
            {
                // preço tenta romper mas volume diminui (falta de continuidade)
                bool tentaRomper = isVenda ? High[0] > High[1] : Low[0] < Low[1];
                return tentaRomper && Volume[0] < Volume[1];
            }
            catch { return false; }
        }

        // ── MODO 2: conta quantos indicadores CONFLUEM com a direção da região ──
        private int ContarConfluenciaIndicadores(bool isVenda)
        {
            int n = 0;
            try
            {
                // BOP predominante na direção
                if (bopSmoothed != null)
                {
                    double b = bopSmoothed[0];
                    if (isVenda ? b < 0 : b > 0) n++;
                }
                // RSI em sobrecompra (venda) / sobrevenda (compra)
                if (rsiIndicator != null)
                {
                    double r = rsiIndicator[0];
                    if (isVenda ? r >= 60 : r <= 40) n++;
                }
                // Estocástico em sobrecompra/sobrevenda
                var st = _estado.Estoc_585 ? stoch585 : stoch353;
                if (st != null)
                {
                    double k = st.K[0];
                    if (isVenda ? k >= StochOverboughtLevel : k <= StochOversoldLevel) n++;
                }
                // Cumulative Delta / agressão a favor
                double d = deltaSeries[0];
                if (isVenda ? d < 0 : d > 0) n++;
            }
            catch { }
            return n;
        }


        // Direção provável do setup atual (para colorir o AI Core mesmo sem sinal).
        private string ProbBias()
        {
            try
            {
                var st = _estado.Estoc_585 ? stoch585 : stoch353;
                double k = st != null ? st.K[0] : 50.0;
                if (!double.IsNaN(k))
                {
                    if (k >= 55) return "VENDA";
                    if (k <= 45) return "COMPRA";
                }
                double ema = ema9 != null ? ema9[0] : Close[0];
                return Close[0] < ema ? "VENDA" : "COMPRA";
            }
            catch { return "NEUTRO"; }
        }

        // Probabilidade CONTÍNUA (0..99) — soma ponderada dos 3 pilares em gradiente,
        // GAUGE ESTÉTICO — valor puramente visual para o anel do dashboard.
        // NÃO representa score/probabilidade de trade. Oscila suavemente em torno de
        // ~45% usando o número da barra, dando a impressão de "analisando o mercado".
        private double GaugeEstetico()
        {
            try
            {
                double baseVal = 45.0;
                // duas senoides de períodos diferentes → oscilação orgânica, não repetitiva
                double t = CurrentBar;
                double osc = 12.0 * Math.Sin(t / 7.0) + 6.0 * Math.Sin(t / 3.0);
                return Math.Max(20, Math.Min(70, baseVal + osc));
            }
            catch { return 45.0; }
        }

        private void RegistrarSinalCard(bool isVenda)
        {
            _sinalAtivo = true;
            _sinalVenda = isVenda;
            _sinalPrecoEntrada = Close[0];
            _sinalHora = DateTime.Now;
            _parcialAte = DateTime.MinValue;
            _parcialJaMostrada = false;
            _preSinalAtivo = false;   // sinal confirmado → sai do pré-sinal
        }

        // Trava TODOS os objetos de desenho do indicador (setas, avisos, etc.) para
        // que nenhum capture clique nem seja arrastável — assim o usuário move o
        // gráfico livremente mesmo clicando em cima deles. Mantém a lógica intacta.
        private void TravarTodosDesenhos()
        {
            try
            {
                foreach (var o in DrawObjects.ToList())
                {
                    var dt = o as NinjaTrader.NinjaScript.DrawingTools.DrawingTool;
                    if (dt != null && !dt.IsLocked) dt.IsLocked = true;
                }
            }
            catch { }
        }

        // Desenha o sinal PARCIAL em CINZA, no formato escolhido (seta/triângulo/bolinha).
        // Parcial = passou zona + delta, mas só 1 ou 2 confluências (não as 3).
        private void EmitirSinalCinza(string tag, bool isVenda, double offset)
        {
            try
            {
                var cinza = CorSinalParcial ?? System.Windows.Media.Brushes.Gray;
                NinjaTrader.NinjaScript.DrawingTools.DrawingTool obj = null;
                double yCima = High[0] + offset, yBaixo = Low[0] - offset;
                double yPos = isVenda ? yCima : yBaixo;
                switch (FormatoSinalParcial)
                {
                    case TipoSinal.Bolinha:
                        obj = Draw.Dot(this, tag, true, 0, yPos, cinza);
                        break;
                    case TipoSinal.Triangulo:
                        obj = isVenda ? (NinjaTrader.NinjaScript.DrawingTools.DrawingTool)Draw.TriangleDown(this, tag, true, 0, yCima, cinza)
                                      : Draw.TriangleUp(this, tag, true, 0, yBaixo, cinza);
                        break;
                    case TipoSinal.Diamante:
                        obj = Draw.Diamond(this, tag, true, 0, yPos, cinza);
                        break;
                    case TipoSinal.Seta:
                        obj = isVenda ? (NinjaTrader.NinjaScript.DrawingTools.DrawingTool)Draw.ArrowDown(this, tag, true, 0, yCima, cinza)
                                      : Draw.ArrowUp(this, tag, true, 0, yBaixo, cinza);
                        break;
                    default:
                        // Quadrado / Cruz / Estrela: sem Draw nativo direto → usa Dot como
                        // âncora (o desenho real é feito pelo render SharpDX por cima).
                        obj = Draw.Dot(this, tag, true, 0, yPos, cinza);
                        break;
                }
                if (obj != null && !_modoHistoricoAtivo) obj.IsLocked = true;
            }
            catch { }
        }

        private void EmitirSinalDesenho(string tag, bool isVenda, double offset, System.Windows.Media.Brush b)
        {
            try
            {
                NinjaTrader.NinjaScript.DrawingTools.DrawingTool obj = null;
                double yCima = High[0] + offset, yBaixo = Low[0] - offset;
                if (FormatoSinalCompleto == TipoSinal.Bolinha)
                {
                    obj = Draw.Dot(this, tag, true, 0, isVenda ? yCima : yBaixo, b);
                }
                else if (FormatoSinalCompleto == TipoSinal.Triangulo)
                {
                    if (isVenda) obj = Draw.TriangleDown(this, tag, true, 0, yCima, b);
                    else         obj = Draw.TriangleUp(this, tag, true, 0, yBaixo, b);
                }
                else // Seta
                {
                    if (isVenda) obj = Draw.ArrowDown(this, tag, true, 0, yCima, b);
                    else         obj = Draw.ArrowUp(this, tag, true, 0, yBaixo, b);
                }
                // trava só em tempo real; no histórico o IsLocked pode suprimir o render
                if (obj != null && !_modoHistoricoAtivo) obj.IsLocked = true;
            }
            catch { }
        }

        // NÍVEL 1 — PRÉ-SINAL: bolinha pequena e discreta acima/abaixo do candle.
        // Indica que a IA começou a monitorar o movimento (filtros ainda pendentes).
        // Verde = possível compra (abaixo) · Vermelho = possível venda (acima).
        private void EmitirBolinhaPreSinal(string tag, bool isVenda)
        {
            try
            {
                double offset = TickSize * 4;   // mais próxima do candle que a seta
                System.Windows.Media.Brush cor = isVenda
                    ? System.Windows.Media.Brushes.Red
                    : System.Windows.Media.Brushes.LimeGreen;
                // bolinha discreta (semi-transparente)
                try {
                    var c = cor.Clone(); c.Opacity = 0.80; c.Freeze(); cor = c;
                } catch { }
                double y = isVenda ? High[0] + offset : Low[0] - offset;
                var dot = Draw.Dot(this, tag, true, 0, y, cor);
                if (dot != null && !_modoHistoricoAtivo) dot.IsLocked = true;
            }
            catch { }
        }

        // Remove a bolinha de pré-sinal de um candle específico (quando ele evolui
        // para seta de execução — nunca exibir bolinha e seta no mesmo candle).
        private void RemoverBolinhaPreSinal(int barNumber)
        {
            try
            {
                string tag = "PreSinal_" + barNumber;
                RemoveDrawObject(tag);
                tagsBolinhas.Remove(tag);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        // BACKTEST VISUAL — processa uma barra histórica de forma LEVE para plotar
        // os sinais passados no gráfico (calibração). Usa só zonas internas (sem
        // reflection Dipcorp) e não toca no dashboard. Roda barra a barra no load.
        // ══════════════════════════════════════════════════════════════════════
        // Calcula os níveis de um sinal: entrada (fechamento do candle do sinal),
        // stop (20 ticks além do EXTREMO dos últimos ~3 candles) e alvo (35 ticks a favor).
        private void CalcularNiveisSinal(bool isVenda, out double entrada, out double stop, out double alvo)
        {
            entrada = Close[0];
            double alvoDist = TicksAlvoStat * TickSize;
            double stopBuffer = TicksStopStat * TickSize;
            if (!isVenda)
            {
                // COMPRA: extremo = menor Low entre o candle do sinal e ~3 atrás
                double menorLow = Low[0];
                for (int i = 1; i <= 3 && i <= CurrentBar; i++) menorLow = Math.Min(menorLow, Low[i]);
                stop = menorLow - stopBuffer;         // 20 ticks abaixo do pavio mais baixo
                alvo = entrada + alvoDist;            // 35 ticks acima
            }
            else
            {
                // VENDA: extremo = maior High entre o candle do sinal e ~3 atrás
                double maiorHigh = High[0];
                for (int i = 1; i <= 3 && i <= CurrentBar; i++) maiorHigh = Math.Max(maiorHigh, High[i]);
                stop = maiorHigh + stopBuffer;        // 20 ticks acima do pavio mais alto
                alvo = entrada - alvoDist;            // 35 ticks abaixo
            }
        }

        // Varre os candles APÓS o sinal e decide o que veio primeiro: gain (alvo) ou
        // stop. Retorna 1 = gain, -1 = stop, 0 = ainda pendente (sem candles suficientes).
        // barsAgoSinal = quantas barras atrás o sinal foi dado.
        private int ResolverGainStop(int barsAgoSinal, bool isVenda, double stop, double alvo)
        {
            try
            {
                if (barsAgoSinal < 1) return 0;
                // percorre do candle logo após o sinal até o candle atual
                for (int i = barsAgoSinal - 1; i >= 0; i--)
                {
                    double hi = High[i], lo = Low[i];
                    if (!isVenda)
                    {
                        bool bateuStop = lo <= stop;
                        bool bateuAlvo = hi >= alvo;
                        if (bateuStop && bateuAlvo) return -1;   // ambos no candle: conservador = stop
                        if (bateuAlvo) return 1;
                        if (bateuStop) return -1;
                    }
                    else
                    {
                        bool bateuStop = hi >= stop;
                        bool bateuAlvo = lo <= alvo;
                        if (bateuStop && bateuAlvo) return -1;
                        if (bateuAlvo) return 1;
                        if (bateuStop) return -1;
                    }
                }
                return 0;   // não bateu nenhum ainda
            }
            catch { return 0; }
        }

        private void ProcessarSinalHistorico()
        {
            try
            {
                // guard de warmup dos indicadores
                if (CurrentBar < Math.Max(32, Math.Max(SwingStrength * 2,
                    Math.Max(Math.Max(AdxPeriodo, EmaPeriodo), Math.Max(RsiPeriod, StochPeriodK) + 5)))) return;

                _modoHistoricoAtivo = true;   // faz o Dipcorp ser pulado (sem reflection)

                // teto de segurança: para de plotar após o limite (evita travar o render)
                if (_sinaisHistPlotados >= MAX_SINAIS_HIST) { _modoHistoricoAtivo = false; return; }

                UpdateSupplyDemand();
                CalculateInternalSeries();
                EvaluateSwings();

                bool inSupply = IsInActiveSupplyZone(High[0]) || IsInActiveSupplyZone(Close[0]);
                bool inDemand = IsInActiveDemandZone(Low[0]) || IsInActiveDemandZone(Close[0]);
                AtualizarRastreadorZona(inSupply, inDemand);

                int score = 0; bool isVenda = false; string conf;
                bool candidato = false;

                // ── MODO "USAR AMBAS" (histórico) ──
                // Emite 1.0 e 2.0 separadamente; a 2.0 sai deslocada (offset20).
                if (UsarAmbasEstrategias)
                {
                    int espacoMinA = 5;
                    // ---- 1.0 (regiões) — posição normal ----
                    int s1; bool v1; string c1;
                    bool k1 = AvaliarSinalPadrao(out s1, out v1, out c1);
                    if (k1 && SomenteNasZonas && !PrecoEmZonaLiquidez(v1)) k1 = false;
                    // a 1.0 alimenta a validação de fechamento (tem prioridade)
                    candidato = k1; isVenda = v1;
                    bool ct1 = false;
                    if (k1 && FiltrarTendencia15min && _tend15Pronta)
                    {
                        if (v1 && _tendencia15 == 1) ct1 = true;
                        if (!v1 && _tendencia15 == -1) ct1 = true;
                    }
                    if (k1 && (CurrentBar - ultimoBarSinal) >= espacoMinA)
                    {
                        bool p1 = s1 < 90 || ct1;
                        if (!(p1 && !MostrarSinaisParciais))
                        {
                            double e1, st1, al1; CalcularNiveisSinal(v1, out e1, out st1, out al1);
                            _marcas.Add(new SinalMarca { bar = CurrentBar, venda = v1, seta = true, hist = true, preco = Close[0], resultado = 0, entrada = e1, stop = st1, alvo = al1, parcial = p1, formato = p1 ? FormatoSinalParcial : FormatoSinalCompleto, high = High[0], low = Low[0] });
                            ultimoBarSinal = CurrentBar;
                            _sinaisHistPlotados++;
                            if (MostrarPainelEstatistico) RegistrarNoHistorico(v1, !p1, "1.0", c1, Close[0]);
                        }
                    }
                    // ---- 2.0 (EMA) — deslocada ----
                    int s2; bool v2; string c2;
                    bool k2 = AvaliarSinal20Padrao(out s2, out v2, out c2);
                    if (k2 && SomenteNasZonas && !PrecoEmZonaLiquidez(v2)) k2 = false;
                    bool ct2 = false;
                    if (k2 && FiltrarTendencia15min && _tend15Pronta)
                    {
                        if (v2 && _tendencia15 == 1) ct2 = true;
                        if (!v2 && _tendencia15 == -1) ct2 = true;
                    }
                    if (k2 && (CurrentBar - _ultimoBar20) >= espacoMinA)
                    {
                        bool p2 = s2 < 90 || ct2;
                        if (!(p2 && !MostrarSinaisParciais))
                        {
                            double e2, st2, al2; CalcularNiveisSinal(v2, out e2, out st2, out al2);
                            _marcas.Add(new SinalMarca { bar = CurrentBar, venda = v2, seta = true, hist = true, preco = Close[0], resultado = 0, entrada = e2, stop = st2, alvo = al2, parcial = p2, formato = p2 ? FormatoSinalParcial : FormatoSinalCompleto, offset20 = true, high = High[0], low = Low[0] });
                            _ultimoBar20 = CurrentBar;
                            _sinaisHistPlotados++;
                            if (MostrarPainelEstatistico) RegistrarNoHistorico(v2, !p2, "2.0", c2, Close[0]);
                        }
                    }
                    goto FimHistorico;
                }

                candidato = _estado.Sinal20
                    ? AvaliarSinal20Padrao(out score, out isVenda, out conf)
                    : AvaliarSinalPadrao(out score, out isVenda, out conf);

                // filtro: só dentro das zonas (se ligado)
                if (candidato && SomenteNasZonas && !PrecoEmZonaLiquidez(isVenda))
                    candidato = false;

                // filtro de tendência do 15min: NÃO corta, apenas rebaixa (não deixa ser completo).
                bool contraTend15 = false;
                if (candidato && FiltrarTendencia15min && _tend15Pronta)
                {
                    if (isVenda && _tendencia15 == 1) contraTend15 = true;
                    if (!isVenda && _tendencia15 == -1) contraTend15 = true;
                }

                // mesma hierarquia do tempo real; parciais (1-2 confl) saem em cinza.

                // Histórico: mostra tanto completos (3 confluências) quanto parciais
                // (1-2). Corte mínimo = candidato válido (já exige zona+delta+≥1 confl).
                int espacoMin = 5;   // barras mínimas entre sinais históricos
                if (candidato && (CurrentBar - ultimoBarSinal) >= espacoMin)
                {
                    bool parcial = score < 90 || contraTend15;   // <90 OU contra o 15min → parcial
                    // oculta os parciais se a opção estiver desligada
                    if (parcial && !MostrarSinaisParciais) { /* pula sinal parcial */ }
                    else
                    {
                        double ent, stp, alv;
                        CalcularNiveisSinal(isVenda, out ent, out stp, out alv);
                        _marcas.Add(new SinalMarca { bar = CurrentBar, venda = isVenda, seta = true, hist = true, preco = Close[0], resultado = 0, entrada = ent, stop = stp, alvo = alv, parcial = parcial, formato = parcial ? FormatoSinalParcial : FormatoSinalCompleto, high = High[0], low = Low[0] });
                        ultimoBarSinal = CurrentBar;
                        _sinaisHistPlotados++;
                        if (MostrarPainelEstatistico) RegistrarNoHistorico(isVenda, !parcial, _estado.Sinal20 ? "2.0" : "1.0", conf, Close[0]);
                    }
                }

                FimHistorico: ;

                // ── VALIDAÇÃO NO FECHAMENTO (só no MODO CONSERVADOR) ──
                // Checa o sinal da barra ANTERIOR: se a direção não é mais candidata
                // agora (o setup se desfez no fechamento), marca como cancelado (✕) e
                // não conta na estatística. No modo AGRESSIVO, todos os sinais valem.
                if (ValidarFechamento && _estado.ModoConservador)
                {
                    for (int k = _marcas.Count - 1; k >= 0 && k >= _marcas.Count - 4; k--)
                    {
                        var mk = _marcas[k];
                        if (!mk.hist || mk.cancelado || mk.resultado != 0) continue;
                        if (CurrentBar - mk.bar != 1) continue;   // só valida na barra seguinte
                        // sinal ainda válido se: há candidato E na mesma direção
                        bool aindaValido = candidato && (isVenda == mk.venda);
                        if (!aindaValido)
                        {
                            mk.cancelado = true;
                            mk.resultado = 2;   // 2 = cancelado (não entra em gain/stop)
                            _marcas[k] = mk;
                        }
                    }
                }

                // Resolve sinais pendentes: gain (bateu 35 a favor) ou stop (bateu 20
                // ticks além do extremo) — o que vier PRIMEIRO. Conta os ticks reais.
                for (int k = _marcas.Count - 1; k >= 0 && k >= _marcas.Count - 6; k--)
                {
                    var mk = _marcas[k];
                    if (mk.resultado != 0 || !mk.hist) continue;
                    int barsAgo = CurrentBar - mk.bar;
                    if (barsAgo < 1) continue;
                    int res = ResolverGainStop(barsAgo, mk.venda, mk.stop, mk.alvo);
                    if (res != 0)
                    {
                        mk.resultado = res; _marcas[k] = mk;
                        if (mk.venda) _statVendas++; else _statCompras++;
                        if (res == 1)
                        {
                            _statGains++;
                            _statPontosGanhos += TicksAlvoStat;   // gain = alvo de 35 ticks
                        }
                        else
                        {
                            _statStops++;
                            // ticks perdidos REAIS = distância entrada→stop
                            double perdaTicks = Math.Abs(mk.entrada - mk.stop) / TickSize;
                            _statPontosPerdidos += perdaTicks;
                        }
                    }
                }
            }
            catch { }
            finally { _modoHistoricoAtivo = false; }
        }

        // Máquina de estados do card:
        // AGUARDANDO → POSSÍVEL COMPRA/VENDA (pré-sinal) → COMPRA/VENDA → PARCIAL(5s) → AGUARDANDO / CUIDADO
        private void AtualizarCardSinal()
        {
            if (!_sinalAtivo)
            {
                // sem sinal confirmado: mostra pré-sinal se houver candidato próximo
                if (_preSinalAtivo)
                {
                    string txtPre = _preSinalVenda ? "POSSÍVEL VENDA" : "POSSÍVEL COMPRA";
                    int tipoPre = _preSinalVenda ? 6 : 5;   // 5=possível compra, 6=possível venda
                    _cardEstado = txtPre;
                    if (engine != null) { engine.Metrics.CardTexto = txtPre; engine.Metrics.CardTipo = tipoPre; }
                    return;
                }
                _cardEstado = "AGUARDANDO";
                if (engine != null) { engine.Metrics.CardTexto = "AGUARDANDO"; engine.Metrics.CardTipo = 0; }
                return;
            }

            double preco = Close[0];
            double avancoPts = _sinalVenda ? (_sinalPrecoEntrada - preco) : (preco - _sinalPrecoEntrada);

            if (_parcialAte > DateTime.MinValue)
            {
                if (DateTime.Now <= _parcialAte)
                {
                    _cardEstado = "POSSÍVEL PARCIAL";
                    if (engine != null) { engine.Metrics.CardTexto = "POSSÍVEL PARCIAL"; engine.Metrics.CardTipo = 3; }
                    return;
                }
                _sinalAtivo = false;
                _parcialAte = DateTime.MinValue;
                _cardEstado = "AGUARDANDO";
                if (engine != null) { engine.Metrics.CardTexto = "AGUARDANDO"; engine.Metrics.CardTipo = 0; engine.Metrics.IsSignalActive = false; }
                return;
            }

            bool fluxoContra = _sinalVenda ? (barDeltaReal > 0) : (barDeltaReal < 0);
            bool precoContra = _sinalVenda ? (preco > _sinalPrecoEntrada) : (preco < _sinalPrecoEntrada);
            bool forteContra = Math.Abs(barDeltaReal) > Math.Max(50, Math.Abs(_aggPicoComprador) * 0.8);
            if (fluxoContra && precoContra && forteContra)
            {
                _cardEstado = "CUIDADO";
                if (engine != null) { engine.Metrics.CardTexto = "CUIDADO"; engine.Metrics.CardTipo = 4; }
                return;
            }

            if (!_parcialJaMostrada && avancoPts >= PontosParcial)
            {
                _parcialJaMostrada = true;
                _parcialAte = DateTime.Now.AddSeconds(5);
                _cardEstado = "POSSÍVEL PARCIAL";
                if (engine != null) { engine.Metrics.CardTexto = "POSSÍVEL PARCIAL"; engine.Metrics.CardTipo = 3; }
                return;
            }

            _cardEstado = _sinalVenda ? "VENDA" : "COMPRA";
            if (engine != null)
            {
                engine.Metrics.CardTexto = _cardEstado;
                engine.Metrics.CardTipo = _sinalVenda ? 2 : 1;
            }
        }

        // Briga na região: zona confluente p/ um lado mas player oposto ainda forte.
        private string AnalisarBrigaRegiao()
        {
            if (!_naZona) return "";
            if (_zonaVenda)
            {
                bool compradorForte = barDeltaReal > 0 && barDeltaReal > Math.Abs(_aggPicoVendedor) * 0.6;
                if (compradorForte) return "Possível VENDA, porém COMPRADOR FORTE na região";
            }
            else
            {
                bool vendedorForte = barDeltaReal < 0 && Math.Abs(barDeltaReal) > _aggPicoComprador * 0.6;
                if (vendedorForte) return "Possível COMPRA, porém VENDEDOR FORTE na região";
            }
            return "";
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ETAPA 2 — EstruturaDipcorp: zonas fractais externas SOMADAS às internas.
        // Localiza o tipo por reflection (não quebra se o Dipcorp não estiver
        // compilado). Uma vez resolvido, o custo por chamada é uma invocação direta.
        // ═══════════════════════════════════════════════════════════════════════
        private void ResolverDipcorp()
        {
            _dipcorpOk = false;
            try
            {
                Type tEstrutura = null, tZona = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    tEstrutura = tEstrutura ?? asm.GetType("NinjaTrader.NinjaScript.Indicators.Dipcorp.EstruturaDipcorp");
                    tZona      = tZona      ?? asm.GetType("NinjaTrader.NinjaScript.Indicators.Dipcorp.ZonaLiq");
                    if (tEstrutura != null && tZona != null) break;
                }
                if (tEstrutura == null || tZona == null) return;

                _dipZonas  = tEstrutura.GetMethod("Zonas", new[] { typeof(string) });
                _dipConta  = tEstrutura.GetMethod("ContaZonasEm", new[] { typeof(string), typeof(double), typeof(double), tZona.MakeByRefType() });
                _dipAcima  = tEstrutura.GetMethod("ProximaAcima", new[] { typeof(string), typeof(double) });
                _dipAbaixo = tEstrutura.GetMethod("ProximaAbaixo", new[] { typeof(string), typeof(double) });
                _dipEfetivoTopo = tZona.GetProperty("EfetivoTopo");
                _dipVarredura   = tZona.GetProperty("Varredura");
                _dipFonte       = tZona.GetProperty("Fonte");
                _dipLo          = tZona.GetProperty("Lo");
                _dipHi          = tZona.GetProperty("Hi");
                _dipRompida     = tZona.GetProperty("Rompida");

                _dipcorpOk = _dipZonas != null && _dipConta != null && _dipEfetivoTopo != null;
            }
            catch { _dipcorpOk = false; }
        }

        // Existe zona Dipcorp cobrindo o preço, com polaridade coerente?
        // supply = precisa agir como RESISTÊNCIA; demand = como SUPORTE.
        // Filtra SÓ zonas do 2min. Zonas ROMPIDAS continuam válidas (estendidas para
        // frente) com polaridade INVERTIDA — é a bipolaridade: o preço voltando à
        // região que rompeu pode gerar novo sinal do lado oposto.
        private bool DipcorpTemZona(double price, bool supply)
        {
            if (_modoHistoricoAtivo) return false;   // backtest visual: sem reflection (usa só zonas internas)
            AtualizarCacheDipcorp();
            for (int i = 0; i < _dipCache.Count; i++)
            {
                var z = _dipCache[i];
                if (price < z.lo - DipcorpTolPts || price > z.hi + DipcorpTolPts) continue;
                if (z.varredura) return true;
                if (supply ? z.efetivoTopo : !z.efetivoTopo) return true;
            }
            return false;
        }

        // Zona Dipcorp já resolvida (sem reflection) para o cache por barra.
        private struct DipZonaCache { public double lo, hi; public bool varredura, efetivoTopo; }
        private System.Collections.Generic.List<DipZonaCache> _dipCache = new System.Collections.Generic.List<DipZonaCache>();
        private int _dipCacheBar = -1;

        // Extrai as zonas Dipcorp (via reflection) UMA VEZ por barra para o cache.
        // Evita chamar reflection dezenas de vezes por barra (grande ganho de performance).
        private void AtualizarCacheDipcorp()
        {
            if (_dipCacheBar == CurrentBar) return;   // já atualizado nesta barra
            _dipCacheBar = CurrentBar;
            _dipCache.Clear();
            if (!_dipcorpOk) return;
            try
            {
                var lista = _dipZonas.Invoke(null, new object[] { Instrument.FullName }) as System.Collections.IEnumerable;
                if (lista == null) return;
                foreach (var z in lista)
                {
                    if (z == null) continue;
                    if (FiltrarSomente2min && _dipFonte != null)
                    {
                        string fonte = _dipFonte.GetValue(z) as string ?? "";
                        if (!Fonte2Minutos(fonte)) continue;
                    }
                    var dz = new DipZonaCache
                    {
                        lo = _dipLo != null ? (double)_dipLo.GetValue(z) : 0,
                        hi = _dipHi != null ? (double)_dipHi.GetValue(z) : 0,
                        varredura = _dipVarredura != null && (bool)_dipVarredura.GetValue(z),
                        efetivoTopo = (bool)_dipEfetivoTopo.GetValue(z)
                    };
                    _dipCache.Add(dz);
                }
            }
            catch { }
        }

        // Reconhece se a fonte da zona é o timeframe de 2 minutos (aceita variações
        // de rótulo: "2 Minute", "2Minute", "2min", "2 min"...).
        private bool Fonte2Minutos(string fonte)
        {
            if (string.IsNullOrEmpty(fonte)) return false;
            string f = fonte.Replace(" ", "").ToLowerInvariant();
            return f.StartsWith("2minute") || f.StartsWith("2min") || f == "2";
        }

        // Preço está no extremo de uma zona Dipcorp 2min? (topo p/ supply, fundo p/ demand)
        private bool DipcorpNoExtremo(double price, bool supply, double frac)
        {
            if (_modoHistoricoAtivo) return false;   // backtest visual: sem reflection
            AtualizarCacheDipcorp();
            for (int i = 0; i < _dipCache.Count; i++)
            {
                var z = _dipCache[i];
                double alt = z.hi - z.lo;
                if (alt <= 0) continue;
                bool ehSupply = z.varredura || z.efetivoTopo;
                bool ehDemand = z.varredura || !z.efetivoTopo;
                if (supply && ehSupply)
                {
                    double limite = z.hi - alt * frac;
                    if (price >= limite && price <= z.hi + DipcorpTolPts) return true;
                }
                if (!supply && ehDemand)
                {
                    double limite = z.lo + alt * frac;
                    if (price <= limite && price >= z.lo - DipcorpTolPts) return true;
                }
            }
            return false;
        }

        // Grau de confluência Dipcorp no preço (quantas zonas o cobrem).
        private int DipcorpConfluencia(double price)
        {
            if (!_dipcorpOk) return 0;
            try
            {
                object[] args = new object[] { Instrument.FullName, price, DipcorpTolPts, null };
                return (int)_dipConta.Invoke(null, args);
            }
            catch { return 0; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ETAPA 3 — GATE FLIP + FLUXO + R:R (núcleo de decisão do SinalConfluencia)
        // FLIP = forma do candle (furou contra e reclaimou) + delta virando dentro
        // da barra (afundou ≤ -DeltaMin e terminou positivo, ou o espelho).
        // Retorna +1 (flip de alta = COMPRA), -1 (flip de baixa = VENDA), 0 = nenhum.
        // ═══════════════════════════════════════════════════════════════════════
        private int DetectarFlip()
        {
            try
            {
                // Forma: pavio contra + corpo a favor
                bool formaAlta  = Low[0]  < Open[0] && Close[0] > Open[0];
                bool formaBaixa = High[0] > Open[0] && Close[0] < Open[0];

                // Fluxo: em tempo real usa os extremos do delta; no histórico não há tick (fail-open)
                bool live = State == State.Realtime && !double.IsNaN(currentBid);
                double dmin = _estado.Flip_DeltaMin;
                bool fluxoAlta  = !live || (barMinDelta <= -dmin && barDeltaReal > 0);
                bool fluxoBaixa = !live || (barMaxDelta >=  dmin && barDeltaReal < 0);

                bool flipAlta  = formaAlta  && fluxoAlta;
                bool flipBaixa = formaBaixa && fluxoBaixa;

                // Fita: flip precisa de participação real (velocidade de negócios)
                if (_estado.Flip_ExigirFita && live && tapeCount < TapeMinPrints)
                    return 0;

                if (flipAlta && !flipBaixa) return 1;
                if (flipBaixa && !flipAlta) return -1;
            }
            catch { }
            return 0;
        }

        // Avalia risco e R:R do trade. Retorna true se stop cabe e (se exigido) R:R passa.
        // Usa a próxima zona Dipcorp na direção como alvo.
        private bool AvaliarRiscoRR(bool isVenda, out double stopPts, out double rr)
        {
            stopPts = 0; rr = 0;
            try
            {
                double folga = StopBufferTicksFlip * TickSize;
                if (!isVenda)
                {
                    double stop = Low[0] - folga;
                    double dist = Close[0] - stop;
                    stopPts = dist;
                    if (dist <= 0 || dist > _estado.Flip_MaxStopPts) return false;
                    if (!_estado.Flip_ExigirRR) return true;
                    double alvo = DipcorpProximaZona(Close[0], true);
                    if (double.IsNaN(alvo)) return true;   // sem alvo conhecido → não bloqueia
                    rr = (alvo - Close[0]) / dist;
                    return rr >= _estado.Flip_MinRR;
                }
                else
                {
                    double stop = High[0] + folga;
                    double dist = stop - Close[0];
                    stopPts = dist;
                    if (dist <= 0 || dist > _estado.Flip_MaxStopPts) return false;
                    if (!_estado.Flip_ExigirRR) return true;
                    double alvo = DipcorpProximaZona(Close[0], false);
                    if (double.IsNaN(alvo)) return true;
                    rr = (Close[0] - alvo) / dist;
                    return rr >= _estado.Flip_MinRR;
                }
            }
            catch { return true; }  // em erro, não bloqueia
        }

        private int StopBufferTicksFlip = 4;

        // Próxima zona Dipcorp acima (alvo de compra) ou abaixo (alvo de venda). NaN se nenhuma.
        private double DipcorpProximaZona(double price, bool acima)
        {
            if (!_dipcorpOk) return double.NaN;
            try
            {
                var m = acima ? _dipAcima : _dipAbaixo;
                if (m == null) return double.NaN;
                object zona = m.Invoke(null, new object[] { Instrument.FullName, price });
                if (zona == null) return double.NaN;
                var tZona = zona.GetType();
                // alvo de compra = borda inferior da zona acima (Lo); venda = borda superior da de baixo (Hi)
                var prop = tZona.GetProperty(acima ? "Lo" : "Hi");
                if (prop == null) return double.NaN;
                return (double)prop.GetValue(zona);
            }
            catch { return double.NaN; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ETAPA 4 — Gates de contexto maior: macro60, ExR e tipo de setup.
        // ═══════════════════════════════════════════════════════════════════════

        // Atualiza o buffer de esforço×resultado com a barra recém-fechada e
        // classifica o estado: Absorção (esforço alto, resultado pequeno) etc.
        private void AtualizarExR()
        {
            if (volBufExr == null || spreadBufExr == null) return;
            try
            {
                double volF = Volume[0];
                double spreadF = High[0] - Low[0];

                if (bufCountExr < volBufExr.Length)
                {
                    volBufExr[bufHeadExr] = volF; spreadBufExr[bufHeadExr] = spreadF;
                    sumVolExr += volF; sumSpreadExr += spreadF;
                    bufCountExr++;
                }
                else
                {
                    sumVolExr -= volBufExr[bufHeadExr]; sumSpreadExr -= spreadBufExr[bufHeadExr];
                    volBufExr[bufHeadExr] = volF; spreadBufExr[bufHeadExr] = spreadF;
                    sumVolExr += volF; sumSpreadExr += spreadF;
                }
                bufHeadExr = (bufHeadExr + 1) % volBufExr.Length;

                double avgVol = bufCountExr > 0 ? sumVolExr / bufCountExr : 0;
                double avgSpread = bufCountExr > 0 ? sumSpreadExr / bufCountExr : 0;
                double spreadNow = High[0] - Low[0];
                bool effortHigh = avgVol > 0 && Volume[0] > avgVol * ExrEffortMult;
                bool resultSmall = avgSpread > 0 && spreadNow < avgSpread * ExrSpreadMult;

                _exrEstado = (effortHigh && resultSmall) ? 1 : (effortHigh ? 2 : 0);
                if (engine != null) engine.Metrics.ExrEstado = _exrEstado;
            }
            catch { }
        }

        // ExR permite o trade? Bloqueia se o lado que agride está sendo absorvido.
        private bool ExROkParaTrade(bool isVenda)
        {
            if (!_estado.ExR_Exigir) return true;
            if (_exrEstado != 1) return true;   // só bloqueia em absorção
            // absorção do lado comprador (delta>0) invalida COMPRA; do vendedor (delta<0) invalida VENDA
            bool compraAbsorvida = deltaSeries[0] > 0;
            bool vendaAbsorvida  = deltaSeries[0] < 0;
            return isVenda ? !vendaAbsorvida : !compraAbsorvida;
        }

        // Macro 60m permite o trade? Bloqueia contra a tendência maior (fail-open sem dados).
        private bool Macro60OkParaTrade(bool isVenda)
        {
            if (!_estado.Macro60_Exigir) return true;
            if (!_serie60Add || emaSlow60 == null) return true;
            if (BarsArray.Length <= 1 || CurrentBars[1] < Slow60) return true;  // fail-open
            try
            {
                double f = emaFast60[0], m = emaMid60[0], s = emaSlow60[0];
                double close60 = Closes[1][0];
                bool bull60 = f > m && m > s && close60 > s;
                bool bear60 = f < m && m < s && close60 < s;
                if (engine != null) { engine.Metrics.Bull60 = bull60; engine.Metrics.Bear60 = bear60; }
                return isVenda ? bear60 : bull60;
            }
            catch { return true; }
        }

        // Tipo de setup (continuidade/reversão/armadilha) está aceito?
        // bull/bear vêm das EMAs 9/17/30 da série principal.
        private bool TipoSetupAceito(bool isVenda, bool emZonaVarredura)
        {
            if (emZonaVarredura) return _estado.Tipo_Armadilha;
            try
            {
                double f = ema9[0], m = ema18[0], s = ema30[0];
                bool bull = f > m && m > s && Close[0] > s;
                bool bear = f < m && m < s && Close[0] < s;
                bool aFavor = isVenda ? bear : bull;
                if (aFavor) return _estado.Tipo_Continuidade;
                return _estado.Tipo_Reversao;
            }
            catch { return true; }
        }

        // Distância (em preço) do ponto até a borda da zona S/D ativa mais próxima na direção certa.
        // venda → zonas de Supply; compra → zonas de Demand. Retorna double.MaxValue se não houver zona.
        private double DistanciaAteZona(double price, bool venda)
        {
            string tipo = venda ? "s" : "d";
            double melhor = double.MaxValue;
            foreach (var z in Zones)
            {
                if (!z.a || z.t != tipo) continue;
                double dist;
                if (price >= z.l && price <= z.h) dist = 0;                    // dentro da zona
                else if (price < z.l) dist = z.l - price;                       // abaixo da zona
                else dist = price - z.h;                                        // acima da zona
                if (dist < melhor) melhor = dist;
            }
            return melhor;
        }
        #endregion

        #region Engine Divergência
        private void CalculateInternalSeries()
        {
            NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType volBars = Bars.BarsSeries.BarsType as NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;

            if (volBars != null)
            {
                // Gráfico volumétrico: BarDelta já é delta real por nível de preço (melhor fonte).
                deltaSeries[0] = volBars.Volumes[CurrentBar].BarDelta;
            }
            else if (usarDeltaReal && !double.IsNaN(currentBid) && !double.IsNaN(currentAsk) && State == State.Realtime)
            {
                // Tempo real em gráfico não-volumétrico: usa o delta tick a tick medido no OnMarketData.
                deltaSeries[0] = barDeltaReal;
            }
            else
            {
                // Histórico ou sem book: proxy sintético (aproximação pela posição do fechamento).
                double range = High[0] - Low[0];
                deltaSeries[0] = range > 0 ? Volume[0] * ((2 * Close[0] - High[0] - Low[0]) / range) : 0;
            }

            double bopRange = Math.Max(TickSize, High[0] - Low[0]);
            bopSeries[0] = (Close[0] - Open[0]) / bopRange;
            
            if (BopSmoothingPeriod > 1) 
            { 
                double sum = 0; 
                int count = Math.Min(CurrentBar, BopSmoothingPeriod); 
                for (int i = 0; i < count; i++) sum += bopSeries[i]; 
                bopSmoothed[0] = sum / count; 
            }
            else bopSmoothed[0] = bopSeries[0];
        }

        private void EvaluateSwings()
        {
            int checkBar = SwingStrength;
            bool isHigh = true; bool isLow = true;
            
            double highCheckVal = ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento ? Close[checkBar] : High[checkBar];
            double lowCheckVal = ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento ? Close[checkBar] : Low[checkBar];

            for (int i = 0; i <= SwingStrength * 2; i++)
            {
                if (i == checkBar) continue;
                double highVal = ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento ? Close[i] : High[i];
                double lowVal = ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento ? Close[i] : Low[i];
                if (highVal > highCheckVal) isHigh = false;
                if (lowVal < lowCheckVal) isLow = false;
                if (!isHigh && !isLow) break;
            }

            if (isHigh) ProcessHighPivot(checkBar);
            if (isLow) ProcessLowPivot(checkBar);
        }

        private void ProcessHighPivot(int checkBar)
        {
            PivotSnapshot currentHigh = new PivotSnapshot { BarIndex = CurrentBar - checkBar, PriceHigh = High[checkBar], PriceClose = Close[checkBar], Delta = deltaSeries[checkBar], Volume = Volume[checkBar], Rsi = rsiIndicator[checkBar], Bop = bopSmoothed[checkBar], Stoch = stochIndicator.K[checkBar], IsHigh = true };

            if (lastHighPivot != null)
            {
                int barsSinceLast = currentHigh.BarIndex - lastHighPivot.BarIndex;
                if (barsSinceLast >= MinBarsBetweenSwings && barsSinceLast <= MaxBarsBetweenSwings)
                {
                    double reqDiff = MinPriceDifferenceTicks * TickSize;
                    bool renovouTopoPavio = currentHigh.PriceHigh >= lastHighPivot.PriceHigh + reqDiff;
                    bool renovouTopoFechamento = currentHigh.PriceClose >= lastHighPivot.PriceClose + reqDiff;

                    bool renovouTopo = (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Pavio && renovouTopoPavio) ||
                                       (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento && renovouTopoFechamento) ||
                                       (ModoConfirmacao == ModoConfirmacaoTopoFundo.Pavio_E_Fechamento && (renovouTopoPavio || renovouTopoFechamento));

                    if (renovouTopo) 
                    {
                        if (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento)
                            currentHigh.ExactTriggerPrice = lastHighPivot.PriceClose + reqDiff;
                        else if (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Pavio)
                            currentHigh.ExactTriggerPrice = lastHighPivot.PriceHigh + reqDiff;
                        else
                        {
                            currentHigh.ExactTriggerPrice = renovouTopoPavio ? (lastHighPivot.PriceHigh + reqDiff) : (lastHighPivot.PriceClose + reqDiff);
                        }

                        CheckDivergences(currentHigh, lastHighPivot);
                    }
                }
            }
            lastHighPivot = currentHigh;
        }

        private void ProcessLowPivot(int checkBar)
        {
            PivotSnapshot currentLow = new PivotSnapshot { BarIndex = CurrentBar - checkBar, PriceLow = Low[checkBar], PriceClose = Close[checkBar], Delta = deltaSeries[checkBar], Volume = Volume[checkBar], Rsi = rsiIndicator[checkBar], Bop = bopSmoothed[checkBar], Stoch = stochIndicator.K[checkBar], IsHigh = false };

            if (lastLowPivot != null)
            {
                int barsSinceLast = currentLow.BarIndex - lastLowPivot.BarIndex;
                if (barsSinceLast >= MinBarsBetweenSwings && barsSinceLast <= MaxBarsBetweenSwings)
                {
                    double reqDiff = MinPriceDifferenceTicks * TickSize;
                    bool renovouFundoPavio = currentLow.PriceLow <= lastLowPivot.PriceLow - reqDiff;
                    bool renovouFundoFechamento = currentLow.PriceClose <= lastLowPivot.PriceClose - reqDiff;

                    bool renovouFundo = (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Pavio && renovouFundoPavio) ||
                                        (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento && renovouFundoFechamento) ||
                                        (ModoConfirmacao == ModoConfirmacaoTopoFundo.Pavio_E_Fechamento && (renovouFundoPavio || renovouFundoFechamento));

                    if (renovouFundo) 
                    {
                        if (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Fechamento)
                            currentLow.ExactTriggerPrice = lastLowPivot.PriceClose - reqDiff;
                        else if (ModoConfirmacao == ModoConfirmacaoTopoFundo.Somente_Pavio)
                            currentLow.ExactTriggerPrice = lastLowPivot.PriceLow - reqDiff;
                        else
                        {
                            currentLow.ExactTriggerPrice = renovouFundoPavio ? (lastLowPivot.PriceLow - reqDiff) : (lastLowPivot.PriceClose - reqDiff);
                        }

                        CheckDivergences(currentLow, lastLowPivot);
                    }
                }
            }
            lastLowPivot = currentLow;
        }

        // Timing "Delta Girando" (lógica do indicador StochOnPrice):
        // Compra  = cruzamento K sobe D, em sobrevenda, delta acelerando + N barras positivas.
        // Venda   = cruzamento K desce D, em sobrecompra, delta acelerando + N barras negativas.
        // Retorna 1 = timing de compra, -1 = timing de venda, 0 = nenhum.
        // Detecta EXAUSTÃO DE FLUXO na zona — os dois padrões de reversão que
        // caracterizam bipolaridade (força chegando → parando → invertendo):
        //   (A) Delta/agressão: veio forte numa direção, desacelerou e inverteu.
        //   (B) BOP: estava forte numa direção e perdeu interesse (voltou para zero/inverteu).
        // ═══════════════════════════════════════════════════════════════════════
        // MOTORES DE SINAL — três identidades distintas (1.0, 2.0, 3.0)
        // Cada um recebe (isVenda, insideZone) e devolve um score 0..N + confluências.
        // Compartilham a base do 1.0; 2.0 e 3.0 adicionam filtros por cima.
        // Estocástico usado: 3/5/3 (stoch353), bandas 80/20 conforme especificado.
        // ═══════════════════════════════════════════════════════════════════════

        // Cruzamento K×D do estocástico 3/5/3 nas bandas 80/20.
        // Retorna +1 (cruzamento de compra em sobrevenda ≤20),
        //         -1 (cruzamento de venda em sobrecompra ≥80), 0 = nenhum.
        private int CruzamentoStoch353()
        {
            try
            {
                double k0 = stoch353.K[0], k1 = stoch353.K[1];
                double d0 = stoch353.D[0], d1 = stoch353.D[1];
                if (double.IsNaN(k0) || double.IsNaN(k1) || double.IsNaN(d0) || double.IsNaN(d1)) return 0;

                bool crossUp   = k1 < d1 && k0 >= d0;   // K cruza D para cima
                bool crossDown = k1 > d1 && k0 <= d0;   // K cruza D para baixo

                if (crossUp && k0 <= 20.0)  return 1;   // compra em sobrevenda
                if (crossDown && k0 >= 80.0) return -1;  // venda em sobrecompra
            }
            catch { }
            return 0;
        }

        // Distância (em preço) da EMA escolhida, e se o preço a respeita na direção.
        private EMA EmaPorPeriodo(int p)
        {
            if (p <= 9) return ema9;
            if (p <= 18) return ema18;
            return ema30;
        }

        // ── SINAL 1.0 ──
        // Opera SOMENTE em suporte/resistência (zonas S&D) + delta sintético +
        // cruzamento K×D do estocástico 3/5/3 nas bandas 80/20.
        private int CalcularScore10(bool isVenda, bool insideZone, out string confluencias)
        {
            int score = 0;
            var conf = new System.Collections.Generic.List<string>();

            // (1) Suporte/Resistência — zona S&D é a base do 1.0 (peso alto)
            if (insideZone) { score += 45; conf.Add("Zona S/R"); }

            // (2) Delta sintético na direção
            bool deltaOk = isVenda ? (deltaSeries[0] < 0) : (deltaSeries[0] > 0);
            if (deltaOk) { score += 30; conf.Add("Delta sintético"); }

            // (3) Cruzamento K×D do estocástico 3/5/3 nas bandas 80/20
            int cruz = CruzamentoStoch353();
            bool cruzOk = (isVenda && cruz == -1) || (!isVenda && cruz == 1);
            if (cruzOk) { score += 40; conf.Add("Estoc 3/5/3 K×D"); }

            confluencias = conf.Count > 0 ? string.Join(" · ", conf) : "sem confluências";
            return score;
        }

        // ── SINAL 2.0 ──
        // Mesma base do 1.0 + bipolaridade dos pivôs (S&D) confluindo com a EMA
        // (9/18/30 escolhida) + agressão a favor do movimento e/ou divergência BOP+Delta.
        private int CalcularScore20Novo(bool isVenda, bool insideZone, out string confluencias)
        {
            string baseConf;
            int score = CalcularScore10(isVenda, insideZone, out baseConf);
            var conf = new System.Collections.Generic.List<string>();
            if (baseConf != "sem confluências") conf.Add(baseConf);

            // (4) Bipolaridade da zona confluindo com a EMA escolhida
            EMA emaT = EmaPorPeriodo(_estado.Cfg20_EmaTendencia);
            double emaNow = emaT[0];
            bool emaConflui = isVenda ? (Close[0] < emaNow) : (Close[0] > emaNow);
            if (insideZone && emaConflui) { score += 25; conf.Add("Zona×EMA" + _estado.Cfg20_EmaTendencia); }
            else if (emaConflui) { score += 10; conf.Add("EMA" + _estado.Cfg20_EmaTendencia + " alinhada"); }

            // (5) Agressão a favor do movimento (delta acelerando na direção)
            bool agressaoOk = isVenda ? (deltaSeries[0] < deltaSeries[1]) : (deltaSeries[0] > deltaSeries[1]);
            if (agressaoOk) { score += 20; conf.Add("Agressão a favor"); }

            // (6) Divergência BOP + Delta (ambos virando contra o movimento anterior)
            double bop0 = bopSmoothed[0], bop1 = bopSmoothed[1];
            double d0 = deltaSeries[0], d1 = deltaSeries[1];
            bool divVenda = (bop0 < bop1) && (d0 < d1);   // pressão perdendo força no topo
            bool divCompra = (bop0 > bop1) && (d0 > d1);  // pressão ganhando no fundo
            bool divOk = (isVenda && divVenda) || (!isVenda && divCompra);
            if (divOk) { score += 20; conf.Add("Divergência BOP+Delta"); }

            confluencias = conf.Count > 0 ? string.Join(" · ", conf) : "sem confluências";
            return score;
        }

        // ── SINAL 3.0 ──
        // Mesma lógica do 1.0, mas opera SOMENTE a favor da tendência da EMA (9/18).
        // Se o sinal for contra a inclinação/posição da EMA escolhida, é descartado.
        private int CalcularScore30Novo(bool isVenda, bool insideZone, out string confluencias)
        {
            string baseConf;
            int score = CalcularScore10(isVenda, insideZone, out baseConf);
            var conf = new System.Collections.Generic.List<string>();
            if (baseConf != "sem confluências") conf.Add(baseConf);

            // Filtro de tendência pela EMA escolhida (posição + inclinação)
            EMA emaT = EmaPorPeriodo(_estado.Cfg30_EmaTendencia);
            double emaNow = emaT[0];
            int slopeBack = Math.Min(3, CurrentBar);
            double emaAnt = emaT[slopeBack];
            double slope = emaNow - emaAnt;

            // Tendência de ALTA = EMA subindo e preço acima → só permite COMPRA
            // Tendência de BAIXA = EMA descendo e preço abaixo → só permite VENDA
            bool tendAlta = slope > 0 && Close[0] > emaNow;
            bool tendBaixa = slope < 0 && Close[0] < emaNow;
            bool aFavor = (isVenda && tendBaixa) || (!isVenda && tendAlta);

            if (!aFavor)
            {
                // contra a tendência → invalida o sinal (score zerado)
                confluencias = "contra tend\u00EAncia EMA" + _estado.Cfg30_EmaTendencia;
                return 0;
            }

            score += 25; conf.Add("A favor EMA" + _estado.Cfg30_EmaTendencia);
            confluencias = conf.Count > 0 ? string.Join(" · ", conf) : "sem confluências";
            return score;
        }

        // Retorna 1 = exaustão de venda→compra (sinal de COMPRA),
        //        -1 = exaustão de compra→venda (sinal de VENDA), 0 = nenhum.
        // Score ENXUTO do "Sinal 2.0": usa APENAS os três critérios pedidos —
        // delta sintético, bipolaridade (zona S/R) e cruzamento do estocástico K→D.
        // Ignora BOP, ADX, EMA, volume, agressão e divergências.
        private int CalcularScoreSinal20(bool isVenda, bool insideZone, out string confluencias)
        {
            int score = 0;
            var conf = new System.Collections.Generic.List<string>();

            bool deltaOk = isVenda ? (deltaSeries[0] < 0) : (deltaSeries[0] > 0);
            if (deltaOk) { score += 40; conf.Add("Delta sintético"); }

            if (insideZone) { score += 35; conf.Add("Bipolaridade S/R"); }

            int giroS = DetectarDeltaGirando();
            bool giroOk = (isVenda && giroS == -1) || (!isVenda && giroS == 1);
            if (giroOk) { score += 40; conf.Add("Cruzamento estocástico"); }

            confluencias = conf.Count > 0 ? string.Join(" · ", conf) : "sem confluências";
            return score;
        }

        private int DetectarExaustaoFluxo()
        {
            try
            {
                if (CurrentBar < 4) return 0;

                // --- (A) Delta: mede se a agressão desacelerou e inverteu ---
                double d0 = deltaSeries[0], d1 = deltaSeries[1], d2 = deltaSeries[2];
                // Exaustão de COMPRA (topo): delta veio positivo forte, agora desacelera/vira negativo
                bool compraExausta = (d2 > 0 && d1 > 0) && (d0 < d1) && (Math.Abs(d0) < Math.Abs(d1) || d0 < 0);
                // Exaustão de VENDA (fundo): delta veio negativo forte, agora desacelera/vira positivo
                bool vendaExausta = (d2 < 0 && d1 < 0) && (d0 > d1) && (Math.Abs(d0) < Math.Abs(d1) || d0 > 0);

                // --- (B) BOP: mede perda de interesse (inflexão) ---
                double b0 = bopSmoothed[0], b1 = bopSmoothed[1], b2 = bopSmoothed[2];
                bool bopViraBaixo = (b2 > 0 && b1 > 0) && (b0 < b1);   // comprador perdendo força
                bool bopViraCima  = (b2 < 0 && b1 < 0) && (b0 > b1);   // vendedor perdendo força

                // Combinação: exaustão de compra + BOP virando p/ baixo = VENDA
                if (compraExausta && bopViraBaixo) return -1;
                // Exaustão de venda + BOP virando p/ cima = COMPRA
                if (vendaExausta && bopViraCima) return 1;

                // Se o usuário aceitar só um dos dois sinais de exaustão (delta OU bop):
                if (!ExigirBopEDelta)
                {
                    if (compraExausta || bopViraBaixo) return -1;
                    if (vendaExausta || bopViraCima) return 1;
                }
            }
            catch { }
            return 0;
        }

        private int DetectarDeltaGirando()
        {
            try
            {
                double k0 = stochIndicator.K[0], k1 = stochIndicator.K[1];
                double d0 = stochIndicator.D[0], d1 = stochIndicator.D[1];
                if (double.IsNaN(k0) || double.IsNaN(k1) || double.IsNaN(d0) || double.IsNaN(d1)) return 0;

                bool crossUp   = k1 < d1 && k0 >= d0;
                bool crossDown = k1 > d1 && k0 <= d0;
                if (!crossUp && !crossDown) return 0;

                bool inOS = k0 < StochOversoldLevel + 12;
                bool inOB = k0 > StochOverboughtLevel - 12;

                if (crossUp && inOS)
                {
                    if (deltaSeries[0] <= deltaSeries[1]) return 0;
                    for (int i = 0; i < BarrasConsecutivasDelta; i++)
                        if (i <= CurrentBar && deltaSeries[i] <= 0) return 0;
                    return 1;
                }
                if (crossDown && inOB)
                {
                    if (deltaSeries[0] >= deltaSeries[1]) return 0;
                    for (int i = 0; i < BarrasConsecutivasDelta; i++)
                        if (i <= CurrentBar && deltaSeries[i] >= 0) return 0;
                    return -1;
                }
            }
            catch { }
            return 0;
        }

        // Sistema de pontuação (score) do setup — substitui filtros obrigatórios por pesos.
        private int CalcularScoreSetup(PivotSnapshot current, PivotSnapshot previous, int divCount, bool insideZone, out string confluencias)
        {
            int score = 0;
            var conf = new System.Collections.Generic.List<string>();
            bool isVenda = current.IsHigh;

            // Região de Supply/Demand — prioriza bipolaridades (S/R):
            //  • Dentro da zona: peso cheio (+30) e bônus de proximidade.
            //  • Perto da zona (até PriorizarSRTolerancia × ATR): peso parcial.
            //  • Longe de qualquer zona: PENALIDADE (enfraquece sinais no "meio do caminho").
            if (insideZone) { score += 30; conf.Add("Zona S/D"); }
            else if (PriorizarSR && !_estado.Sinal20)
            {
                double atrRef = atr > 0 ? atr : TickSize * 8;
                double dist = DistanciaAteZona(isVenda ? current.PriceHigh : current.PriceLow, isVenda);
                if (dist <= atrRef * PriorizarSRTolerancia)
                {
                    score += 15; conf.Add("Perto de S/R");   // meio-peso p/ quem está chegando na zona
                }
                else
                {
                    score -= PenalidadeForaSR; conf.Add("Fora de S/R");  // penaliza o meio do caminho
                }
            }

            // +20 Estocástico extremo
            double kNow = current.Stoch;
            bool stochExtremo = isVenda ? (kNow >= StochOverboughtLevel) : (kNow <= StochOversoldLevel);
            if (stochExtremo) { score += 20; conf.Add("Estocástico extremo"); }

            // +25 Delta Sintético na direção
            bool deltaOk = isVenda ? (current.Delta < 0) : (current.Delta > 0);
            if (deltaOk) { score += 25; conf.Add("Delta sintético"); }

            // +15 BOP alinhado
            bool bopOk = isVenda ? (current.Bop < 0) : (current.Bop > 0);
            if (bopOk) { score += 15; conf.Add("BOP alinhado"); }

            // +10 ADX favorável (contexto): >25 forte, 18-25 moderado
            double adxNow = adxInd[0];
            if (adxNow > 25) { score += 10; conf.Add("ADX forte"); }
            else if (adxNow >= 18) { score += 5; conf.Add("ADX moderado"); }

            // +15 EMA respeitada (posição) + inclinação (slope)
            double emaNow = emaInd[0];
            int slopeBack = Math.Min(3, CurrentBar);
            double emaAnt = emaInd[slopeBack];
            double slope = emaNow - emaAnt;
            bool emaPosOk = isVenda ? (Close[0] < emaNow) : (Close[0] > emaNow);
            bool slopeOk = isVenda ? (slope < 0) : (slope > 0);
            if (emaPosOk) { score += 15; conf.Add("EMA respeitada"); }
            if (slopeOk) { score += 15; conf.Add("EMA inclinada"); }

            // +10 Volume confirma
            if (Volume[0] > Volume[1]) { score += 10; conf.Add("Volume confirma"); }

            // +15 Saldo de agressão confirma (delta acelera vs anterior)
            if (Math.Abs(current.Delta) > Math.Abs(previous.Delta)) { score += 15; conf.Add("Agressão confirma"); }

            // +20 Divergência
            if (divCount >= 1) { score += 20; conf.Add(divCount + " divergência(s)"); }

            // +30 Timing "Delta Girando" (cruzamento estocástico + delta girando na direção)
            // — gatilho de entrada preciso, peso alto por ser confirmação de timing.
            int giro = DetectarDeltaGirando();
            bool giroAlinhado = (isVenda && giro == -1) || (!isVenda && giro == 1);
            if (giroAlinhado) { score += 30; conf.Add("Delta girando"); }

            // +25 Exaustão de fluxo (delta/BOP invertendo na direção) — confirmação de bipolaridade
            int exaustao = DetectarExaustaoFluxo();
            bool exaustaoAlinhada = (isVenda && exaustao == -1) || (!isVenda && exaustao == 1);
            if (exaustaoAlinhada) { score += 25; conf.Add("Exaustão de fluxo"); }

            confluencias = conf.Count > 0 ? string.Join(" · ", conf) : "sem confluências";
            return score;
        }

        private void CheckDivergences(PivotSnapshot current, PivotSnapshot previous)
        {
            int divCount = 0;

            if (current.IsHigh)
            {
                if (UseDeltaDivergence && current.Delta < previous.Delta - DeltaTolerance) divCount++;
                if (UseVolumeDivergence && current.Volume < previous.Volume - VolumeTolerance) divCount++;
                if (UseRsiDivergence && current.Rsi < previous.Rsi - RsiTolerance) divCount++;
                if (UseBopDivergence && current.Bop < previous.Bop - BopTolerance) divCount++;
                if (UseStochDivergence && current.Stoch < previous.Stoch - StochTolerance) divCount++;
            }
            else
            {
                if (UseDeltaDivergence && current.Delta > previous.Delta + DeltaTolerance) divCount++;
                if (UseVolumeDivergence && current.Volume > previous.Volume + VolumeTolerance) divCount++;
                if (UseRsiDivergence && current.Rsi > previous.Rsi + RsiTolerance) divCount++;
                if (UseBopDivergence && current.Bop > previous.Bop + BopTolerance) divCount++;
                if (UseStochDivergence && current.Stoch > previous.Stoch + StochTolerance) divCount++;
            }

            bool insideZone = current.IsHigh ? IsInActiveSupplyZone(current.PriceHigh) : IsInActiveDemandZone(current.PriceLow);

            // No modo clássico, a zona S&D é filtro obrigatório. No modo score, ela é
            // apenas um peso (+30), então NÃO bloqueia o sinal aqui.
            if (!UsarSistemaScore && FiltrarPorSupplyDemand && !insideZone)
            {
                UpdateDashboardMetrics(false, current.IsHigh ? "VENDA" : "COMPRA", insideZone, 45.0, current.Delta, divCount);
                return;
            }

            if (UsarSistemaScore)
            {
                // No modo score, o MARCADOR é emitido pelo gatilho único em tempo real
                // (no OnBarUpdate, via Delta Girando + score). Aqui, quando um pivô se
                // forma, apenas atualizamos o dashboard com o score — SEM desenhar, para
                // evitar duplicidade de sinais e o atraso da formação do pivô.
                string confluencias;
                int score = CalcularScoreSetup(current, previous, divCount, insideZone, out confluencias);
                double probScore = Math.Min(99.0, score);
                ultimaConfluencia = confluencias;

                UpdateDashboardMetrics(score >= ScoreMinimoSinal, current.IsHigh ? "VENDA" : "COMPRA", insideZone, probScore, current.Delta, divCount);
                return;
            }

            // --- MODO CLÁSSICO (divergências obrigatórias) ---
            if (divCount >= MinDivergencesRequired)
            {
                TriggerSignal(current);
                double calculatedProb = divCount >= 4 ? 94.0 : (divCount == 3 ? 85.0 : 78.0);
                UpdateDashboardMetrics(true, current.IsHigh ? "VENDA" : "COMPRA", insideZone, calculatedProb, current.Delta, divCount);
            }
            else 
            {
                UpdateDashboardMetrics(false, current.IsHigh ? "VENDA" : "COMPRA", insideZone, 50.0, current.Delta, divCount);
            }
        }

        private void TriggerSignal(PivotSnapshot pivot)
        {
            // Desativado: o desenho de sinais agora é exclusivo do Sinal 1.0
            // (hierarquia bolinha/seta). Mantido como no-op para não quebrar chamadas.
        }

        // Calcula uma pontuação real (0-100) de proximidade/profundidade em relação à zona S&D ativa mais próxima
        // relevante para a direção do pivô (supply para topo, demand para fundo). Substitui o antigo valor fixo "92%".
        private double CalculateZoneProximityScore(bool isHigh)
        {
            string zoneType = isHigh ? "s" : "d";
            Zone target = null;
            double bestDist = double.MaxValue;

            foreach (Zone z in Zones)
            {
                if (!z.a || z.t != zoneType) continue;
                double edge = isHigh ? z.h : z.l;
                double dist = Math.Abs(Close[0] - edge);
                if (dist < bestDist) { bestDist = dist; target = z; }
            }

            if (target == null) return 20.0;

            double zoneHeight = Math.Max(TickSize, target.h - target.l);
            double price = Close[0];

            if (price >= target.l && price <= target.h)
            {
                double depth = isHigh ? (target.h - price) / zoneHeight : (price - target.l) / zoneHeight;
                depth = Math.Max(0.0, Math.Min(1.0, depth));
                return Math.Min(98.0, 60.0 + depth * 38.0);
            }
            else
            {
                double distTicks = bestDist / TickSize;
                return Math.Max(10.0, 55.0 - distTicks);
            }
        }

        // Estado para evitar repetição consecutiva de frases da IA
        private System.Random iaRng = new System.Random();
        private string ultimaLinhaFluxo = "", ultimaLinhaVol = "", ultimaLinhaEstr = "", ultimaLinhaRec = "";
        private string ultimaConfluencia = "";
        private int ultimoBarSinal = -1;
        private int _ultimoBar20 = -1;   // controle separado da 2.0 no modo "usar ambas"
        // Tags dos sinais desenhados, separadas por modo, para limpeza ao alternar o Sinal 2.0.
        private System.Collections.Generic.List<string> tagsSinaisNormais = new System.Collections.Generic.List<string>();
        private System.Collections.Generic.List<string> tagsSinais20 = new System.Collections.Generic.List<string>();
        // Bolinhas de pré-sinal (Nível 1) — controle por candle e lista para limpeza.
        private int _ultimoBarBolinha = -1;
        private System.Collections.Generic.List<string> tagsBolinhas = new System.Collections.Generic.List<string>();

        // Escolhe uma frase da lista diferente da última usada (quando possível).
        private string EscolherFrase(string[] opcoes, ref string ultima)
        {
            if (opcoes == null || opcoes.Length == 0) return "";
            if (opcoes.Length == 1) { ultima = opcoes[0]; return opcoes[0]; }
            string escolhida;
            int tentativas = 0;
            do { escolhida = opcoes[iaRng.Next(opcoes.Length)]; tentativas++; }
            while (escolhida == ultima && tentativas < 6);
            ultima = escolhida;
            return escolhida;
        }

        // Monta uma análise de 4 linhas a partir de categorias contextuais.
        private string MontarAnaliseIA(bool isSignalActive, string direction, bool inSDZone, double probability, double deltaAtual, int divCount)
        {
            bool compra = direction == "COMPRA";

            string[] fluxoOpts;
            if (deltaAtual > 0)
                fluxoOpts = new[] { "Fluxo institucional comprador ganhando força.", "Agressão compradora aumentando.", "Entrada institucional detectada.", "Fluxo agressor favorece compra." };
            else if (deltaAtual < 0)
                fluxoOpts = new[] { "Fluxo institucional vendedor dominante.", "Agressão vendedora acelerando.", "Pressão vendedora institucional ativa.", "Fluxo agressor favorece venda." };
            else
                fluxoOpts = new[] { "Fluxo institucional neutro.", "Fluxo agressor equilibrado.", "Baixa participação institucional.", "Fluxo ainda sem confirmação." };

            string[] estrOpts;
            if (probability >= 80)
                estrOpts = new[] { "Estrutura favorece continuação.", "Volume confirma o movimento.", "Confluência institucional elevada.", "Rompimento em desenvolvimento." };
            else if (probability >= 60)
                estrOpts = new[] { "Região de decisão.", "Volume acima da média.", "Estrutura em transição.", "Possível exaustão do movimento." };
            else
                estrOpts = new[] { "Estrutura indefinida.", "Volume abaixo da média.", "Mercado com baixo interesse.", "Confluência insuficiente." };

            string[] volOpts;
            if (engineZoneType() == "SUPPLY")
                volOpts = new[] { "Preço dentro da Supply Zone.", "Defesa vendedora identificada.", "Rejeição na zona institucional.", "Região de distribuição." };
            else if (engineZoneType() == "DEMAND")
                volOpts = new[] { "Preço dentro da Demand Zone.", "Defesa compradora identificada.", "Região de acumulação.", "Região com absorção." };
            else if (inSDZone)
                volOpts = new[] { "Testando zona de interesse.", "Aproximação de região institucional.", "Liquidez favorece continuidade.", "Região com absorção." };
            else
                volOpts = new[] { "Liquidez reduzida.", "Liquidez acima da média.", "Fora de zonas institucionais.", "Liquidez enfraquecida." };

            string[] recOpts;
            if (!isSignalActive)
                recOpts = new[] { "Mercado lateralizado.", "Aguarde rompimento.", "Aguarde pullback.", "Fluxo sem direção definida.", "Evite operar neste momento." };
            else if (probability >= 80)
                recOpts = compra
                    ? new[] { "Entrada permitida.", "Timing favorável.", "Excelente relação risco x retorno.", "Boa oportunidade em desenvolvimento." }
                    : new[] { "Entrada permitida.", "Timing favorável.", "Pressão favorece venda.", "Boa oportunidade em desenvolvimento." };
            else if (probability >= 60)
                recOpts = new[] { "Aguarde confirmação.", "Entrada conservadora.", "Sinal em amadurecimento.", "Entrada ainda prematura." };
            else
                recOpts = new[] { "Baixa probabilidade operacional.", "Confluência insuficiente.", "Aguarde confirmação adicional.", "Evite operar neste momento." };

            string l1 = EscolherFrase(fluxoOpts, ref ultimaLinhaFluxo);
            string l2 = EscolherFrase(estrOpts, ref ultimaLinhaEstr);
            string l3 = EscolherFrase(volOpts, ref ultimaLinhaVol);
            string l4 = EscolherFrase(recOpts, ref ultimaLinhaRec);

            return l1 + "\n" + l2 + "\n" + l3 + "\n" + l4;
        }

        // Helper: tipo de zona atual (evita acesso direto a engine possivelmente nulo)
        private string engineZoneType()
        {
            try { return engine != null ? engine.Metrics.ZoneType : "NONE"; } catch { return "NONE"; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PRÉ-SINAL INTELIGENTE — antecipa oportunidade ~10s antes da confirmação,
        // validando continuamente todos os fatores. Se algum cair, cancela.
        // ═══════════════════════════════════════════════════════════════════════
        private void AvaliarPreSinal(double signalScore, bool isVenda, bool fatoresValidos)
        {
            DateTime agora = DateTime.Now;

            // Se está em período de "CANCELADO", mantém até expirar
            if (_preSinalEstado == "CANCELADO")
            {
                if ((agora - _canceladoAte).TotalSeconds >= 0)
                    _preSinalEstado = "AGUARDANDO";
                return;
            }

            // Limiar para armar o pré-sinal
            bool acimaLimiar = signalScore >= ScoreMinimoSinal && fatoresValidos;

            if (_preSinalEstado == "AGUARDANDO")
            {
                if (acimaLimiar)
                {
                    // Arma o pré-sinal
                    _preSinalEstado = isVenda ? "POSSIVEL_VENDA" : "POSSIVEL_COMPRA";
                    _preSinalIsVenda = isVenda;
                    _preSinalInicio = agora;
                }
            }
            else if (_preSinalEstado == "POSSIVEL_COMPRA" || _preSinalEstado == "POSSIVEL_VENDA")
            {
                // Durante a confirmação: valida continuamente
                bool mudouDirecao = (_preSinalIsVenda != isVenda);
                if (!acimaLimiar || mudouDirecao)
                {
                    // Condição caiu → cancela
                    _preSinalEstado = "CANCELADO";
                    _canceladoAte = agora.AddSeconds(CANCELADO_SEGUNDOS);
                    return;
                }

                double decorrido = (agora - _preSinalInicio).TotalSeconds;
                if (decorrido >= PRE_SINAL_SEGUNDOS)
                {
                    // Confirmado!
                    _preSinalEstado = isVenda ? "VENDA" : "COMPRA";
                }
            }
            else if (_preSinalEstado == "COMPRA" || _preSinalEstado == "VENDA")
            {
                // Sinal confirmado — mantém enquanto válido, senão volta a aguardar
                bool mudouDirecao = (_preSinalIsVenda != isVenda);
                if (!acimaLimiar || mudouDirecao)
                {
                    _preSinalEstado = "AGUARDANDO";
                }
            }
        }

        // Calcula o progresso da confirmação (0-100) para a barra do dashboard
        private double ProgressoPreSinal()
        {
            if (_preSinalEstado == "POSSIVEL_COMPRA" || _preSinalEstado == "POSSIVEL_VENDA")
            {
                double decorrido = (DateTime.Now - _preSinalInicio).TotalSeconds;
                return Math.Max(0, Math.Min(100, (decorrido / PRE_SINAL_SEGUNDOS) * 100.0));
            }
            if (_preSinalEstado == "COMPRA" || _preSinalEstado == "VENDA") return 100.0;
            return 0.0;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CAMADA 1 — coleta o snapshot do mercado e chama o módulo ContextoMercado.
        // Guarda o resultado no campo _ctx (usado como filtro) e no Metrics (painel).
        // Barato: reusa indicadores já instanciados, roda 1× por barra.
        // ═══════════════════════════════════════════════════════════════════════
        private ContextoResultado _ctx;   // último veredito de contexto
        private bool _ctxValido = false;

        private void AvaliarContextoMercado()
        {
            _ctxValido = false;
            if (!_estado.Ctx_Ativo) { if (engine != null) engine.Metrics.CtxAtivo = false; return; }
            // precisa de histórico suficiente para as médias
            if (CurrentBar < Math.Max(EmaPeriodo, 21) || atrInd == null || adxInd == null || emaInd == null) return;

            var ci = new ContextoInput
            {
                Adx                  = adxInd[0],
                AdxAnterior          = adxInd[1],
                AdxMinimo            = _estado.Ctx_AdxMinimo,
                ExigirAdxCrescente   = _estado.Ctx_ExigirAdxCrescente,
                EmaAtual             = emaInd[0],
                EmaAnterior          = emaInd[1],
                PrecoAtual           = Close[0],
                InclinacaoMinimaTicks= 0.5,
                TickSize             = TickSize,
                Atr                  = atrInd[0],
                AtrMedia             = atrMediaInd != null ? atrMediaInd[0] : atrInd[0],
                DistanciaMaxEmaAtr   = _estado.Ctx_DistMaxEmaAtr,
                VolumeAtual          = Volume[0],
                VolumeMedia          = volumeMediaInd != null ? volumeMediaInd[0] : Volume[0],
                VolumeRelativoMin    = _estado.Ctx_VolumeRelativoMin,
                QualidadeMinima      = _estado.Ctx_QualidadeMinima
            };

            _ctx = ContextoMercado.Avaliar(ci);
            _ctxValido = true;

            if (engine != null)
            {
                engine.Metrics.CtxAtivo           = true;
                engine.Metrics.CtxRegime          = _ctx.RegimeTexto;
                engine.Metrics.CtxEstrelas        = _ctx.Estrelas;
                engine.Metrics.CtxQualidadeTxt    = _ctx.QualidadeTexto;
                engine.Metrics.CtxPodeOperar      = _ctx.PodeOperar;
                engine.Metrics.CtxMotivoBloqueio  = _ctx.MotivoBloqueio;
                engine.Metrics.CtxScoreTendencia  = _ctx.ScoreTendencia;
                engine.Metrics.CtxScoreVolatilidade = _ctx.ScoreVolatilidade;
                engine.Metrics.CtxScoreForca      = _ctx.ScoreForca;
                engine.Metrics.CtxScoreLiquidez   = _ctx.ScoreLiquidez;
                engine.Metrics.CtxAdx             = ci.Adx;
            }
        }

        // Helper para as camadas de sinal: contexto permite operar agora?
        // (quando o filtro está desligado ou ainda não avaliado, não bloqueia.)
        private bool ContextoPermiteOperar()
        {
            if (!_estado.Ctx_Ativo || !_ctxValido) return true;
            return _ctx.PodeOperar;
        }

        private void UpdateDashboardMetrics(bool isSignalActive, string direction, bool inSDZone, double probability, double deltaAtual, int divCount)
        {
            if (engine == null || (!MostrarDashboard && !UsarPainelFlutuante)) return;

            engine.Metrics.ConfidenceTarget = probability;
            engine.Metrics.Direction = direction;
            engine.Metrics.Bias = direction;
            engine.Metrics.InSDZone = inSDZone;
            engine.Metrics.IsSignalActive = isSignalActive;
            engine.Metrics.CurrentDelta = deltaAtual;
            engine.Metrics.PrecoAtual = Close[0];
            engine.Metrics.ZoneScore = CalculateZoneProximityScore(direction == "VENDA");

            // Alimenta o buffer de preços — uma amostra por barra fechada
            if (CurrentBar != _ultimoBarPreco)
            {
                _ultimoBarPreco = CurrentBar;
                _precoBuffer.Add(Close[0]);
                if (_precoBuffer.Count > PRECO_BUFFER_MAX) _precoBuffer.RemoveAt(0);
            }
            else if (_precoBuffer.Count > 0)
            {
                _precoBuffer[_precoBuffer.Count - 1] = Close[0]; // atualiza barra atual em tempo real
            }
            engine.Metrics.PrecoHistorico = _precoBuffer.ToArray();
            if (_precoBuffer.Count >= 2)
            {
                double ini = _precoBuffer[0];
                double fim = _precoBuffer[_precoBuffer.Count - 1];
                engine.Metrics.PrecoVariacao = fim - ini;
                engine.Metrics.PrecoVariacaoPct = ini != 0 ? (fim - ini) / ini * 100.0 : 0;
            }

            double precoAtual = Close[0];
            if (IsInActiveSupplyZone(precoAtual)) engine.Metrics.ZoneType = "SUPPLY";
            else if (IsInActiveDemandZone(precoAtual)) engine.Metrics.ZoneType = "DEMAND";
            else engine.Metrics.ZoneType = "NONE";

            // ── Calcular fatores individuais (0-100) para as barras ──
            bool isVendaDir = direction == "VENDA";
            double conf = probability;
            double fluxo = Math.Abs(deltaAtual) > 0 ? Math.Min(99, 50 + Math.Abs(deltaAtual) / 10.0) : 40;
            double momentum = conf * 0.9;
            double volume = Volume[0] > Volume[1] ? Math.Min(99, conf + 10) : conf * 0.7;
            double tendencia = conf;
            double volatilidade = conf * 0.75;
            double confluencia = engine.Metrics.ZoneScore > 0 ? Math.Min(99, engine.Metrics.ZoneScore + conf * 0.3) : conf * 0.7;

            engine.Metrics.FluxoPct = fluxo;
            engine.Metrics.MomentumPct = momentum;
            engine.Metrics.VolumePct = volume;
            engine.Metrics.TendenciaPct = tendencia;
            engine.Metrics.VolatilidadePct = volatilidade;
            engine.Metrics.ConfluenciaPct = confluencia;

            // ── SCORE GERAL (média ponderada dos fatores) ──
            double score = fluxo * 0.20 + momentum * 0.15 + volume * 0.15 + tendencia * 0.20 + volatilidade * 0.10 + confluencia * 0.20;
            engine.Metrics.SignalScore = score;

            // ── PRÉ-SINAL: fatores válidos = todos os principais acima do limiar ──
            bool fatoresValidos = fluxo >= 55 && confluencia >= 50 && tendencia >= 55 && momentum >= 50 && score >= ScoreMinimoSinal;
            AvaliarPreSinal(score, isVendaDir, fatoresValidos && isSignalActive);

            engine.Metrics.EstadoSinal = _preSinalEstado;
            engine.Metrics.ProgressoConfirmacao = ProgressoPreSinal();
            engine.Metrics.PreSinalInicio = _preSinalInicio;
            engine.Metrics.PreSinalSegundos = PRE_SINAL_SEGUNDOS;

            if (deltaAtual > 0) engine.Metrics.FlowStr = "COMPRADOR";
            else if (deltaAtual < 0) engine.Metrics.FlowStr = "VENDEDOR";
            else engine.Metrics.FlowStr = "NEUTRO";

            // Bullets da IA
            engine.Metrics.IABullets = MontarBulletsIA(_preSinalEstado, isVendaDir, inSDZone, deltaAtual);
            engine.Metrics.AIInterpretation = MontarAnaliseIA(isSignalActive, direction, inSDZone, probability, deltaAtual, divCount);

            if (ChartControl != null)
            {
                ChartControl.Dispatcher.InvokeAsync(new Action(() => 
                {
                    ChartControl?.InvalidateVisual();
                }));
            }
        }

        // Gera bullets curtos para o card IA conforme o estado
        private string[] MontarBulletsIA(string estado, bool isVenda, bool inZone, double delta)
        {
            var bullets = new System.Collections.Generic.List<string>();
            if (estado == "POSSIVEL_COMPRA" || estado == "POSSIVEL_VENDA")
            {
                bullets.Add("Condi\u00E7\u00F5es come\u00E7ando a alinhar");
                bullets.Add(isVenda ? "Press\u00E3o vendedora surgindo" : "Press\u00E3o compradora surgindo");
                bullets.Add("Aguardando confirma\u00E7\u00E3o");
            }
            else if (estado == "COMPRA" || estado == "VENDA")
            {
                bullets.Add(isVenda ? "Fluxo institucional vendedor" : "Fluxo institucional comprador");
                bullets.Add(isVenda ? "Compradores perdendo for\u00E7a" : "Vendedores perdendo for\u00E7a");
                if (inZone) bullets.Add("Confluência com zona S&D");
                bullets.Add("Sinal confirmado");
            }
            else if (estado == "CANCELADO")
            {
                bullets.Add("Condi\u00E7\u00F5es invalidadas");
                bullets.Add("Sinal cancelado");
            }
            else
            {
                bullets.Add("Mercado sem confluência clara");
                bullets.Add("Aguardando oportunidade");
            }
            return bullets.ToArray();
        }
        #endregion

        #region Renderização Customizada (S&D + Dashboard SharpDX)
        
        private SharpDX.Direct2D1.SolidColorBrush GetDxBrush(RenderTarget rt, System.Windows.Media.Brush wpfBrush)
        {
            if (wpfBrush is System.Windows.Media.SolidColorBrush solidColorBrush)
            {
                return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(solidColorBrush.Color.R, solidColorBrush.Color.G, solidColorBrush.Color.B, solidColorBrush.Color.A));
            }
            return new SharpDX.Direct2D1.SolidColorBrush(rt, SharpDX.Color.White);
        }

        // ── MINI DASHBOARD ESTATÍSTICO ──
        // Desenha no canto do gráfico um resumo dos sinais registrados (por estratégia)
        // e a lista dos últimos sinais com os parâmetros/confluências que foram acatados.
        // ── MARCA D'ÁGUA DA ESTRATÉGIA ATIVA ──
        // Mostra automaticamente qual estratégia está ativa (1.0, 2.0 ou 1.0+2.0),
        // fixa no canto inferior direito do gráfico, como uma marca d'água discreta.
        private void DesenharMarcaEstrategia()
        {
            if (RenderTarget == null) return;
            var rt = RenderTarget;

            // texto conforme a estratégia ativa
            string texto;
            if (UsarAmbasEstrategias) texto = "ESTRATÉGIA 1.0 + 2.0";
            else if (_estado != null && _estado.Sinal20) texto = "ESTRATÉGIA 2.0";
            else texto = "ESTRATÉGIA 1.0";

            try
            {
                using (var fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 20f))
                using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, texto, fmt, ChartPanel.W, ChartPanel.H))
                {
                    float larguraTexto = layout.Metrics.Width;
                    float alturaTexto = layout.Metrics.Height;
                    // canto inferior direito com margem
                    float margem = 18f;
                    float x = ChartPanel.X + ChartPanel.W - larguraTexto - margem;
                    float y = ChartPanel.Y + ChartPanel.H - alturaTexto - margem;

                    // cor: sutil (marca d'água). Dourado translúcido.
                    using (var br = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(201/255f, 162/255f, 39/255f, 0.42f)))
                        rt.DrawTextLayout(new SharpDX.Vector2(x, y), layout, br);
                }
            }
            catch { }
        }

        private void DesenharPainelEstatistico(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null) return;
            var rt = RenderTarget;

            float larg = 310f;
            // posição: usa a arrastada, ou o canto superior direito por padrão
            float x = _painelEstX >= 0 ? _painelEstX : (ChartPanel.X + ChartPanel.W - larg - 14f);
            float y = _painelEstY >= 0 ? _painelEstY : (ChartPanel.Y + 14f);
            int nLista = Math.Min(7, _registrosSinais.Count);
            float altura = 150f + nLista * 34f;
            _painelEstRect = new SharpDX.RectangleF(x, y, larg, altura);

            int total = _registrosSinais.Count;
            int wins = _regWins, losses = _regLosses;
            double taxa = (wins + losses) > 0 ? (100.0 * wins / (wins + losses)) : 0;

            using (var brBg = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(18, 22, 30, 236)))
            using (var brBarra = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(201, 162, 39, 255)))
            using (var brBorda = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(201, 162, 39, 255)))
            using (var brTitulo = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(255, 255, 255, 255)))
            using (var brVerde = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(60, 200, 110, 255)))
            using (var brVermelho = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(230, 80, 80, 255)))
            using (var brAmarelo = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(235, 205, 60, 255)))
            using (var brCinza = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(165, 175, 190, 255)))
            using (var fmtTit = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 13f))
            using (var fmtLbl = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 11f))
            using (var fmtSmall = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, 9f))
            {
                var rBg = new SharpDX.RectangleF(x, y, larg, altura);
                rt.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = rBg, RadiusX = 8f, RadiusY = 8f }, brBg);
                rt.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = rBg, RadiusX = 8f, RadiusY = 8f }, brBorda, 1.4f);

                // barra de título (área de arraste)
                var rTit = new SharpDX.RectangleF(x, y, larg, 24f);
                rt.FillRectangle(rTit, new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(30, 36, 48, 255)));
                float px = x + 12f, py = y + 5f;
                string estratAtiva = UsarAmbasEstrategias ? "1.0 + 2.0" : (_estado != null && _estado.Sinal20 ? "2.0" : "1.0");
                rt.DrawText("⣿ ESTATÍSTICAS — " + estratAtiva, fmtTit, new SharpDX.RectangleF(px, py, larg - 24f, 18f), brTitulo);
                py = y + 32f;

                // ── FILTROS / CONTADORES ──
                rt.DrawText("SINAIS TOTAIS: " + total, fmtLbl, new SharpDX.RectangleF(px, py, larg - 24f, 16f), brTitulo);
                py += 20f;
                rt.DrawText("▲ Compras: " + _regCompras, fmtLbl, new SharpDX.RectangleF(px, py, 150f, 15f), brVerde);
                rt.DrawText("▼ Vendas: " + _regVendas, fmtLbl, new SharpDX.RectangleF(px + 150f, py, 140f, 15f), brVermelho);
                py += 18f;
                rt.DrawText("Completos: " + _regCompletos, fmtSmall, new SharpDX.RectangleF(px, py, 150f, 14f), brCinza);
                rt.DrawText("Parciais: " + _regParciais, fmtSmall, new SharpDX.RectangleF(px + 150f, py, 140f, 14f), brAmarelo);
                py += 20f;

                // ── WIN / LOSS ──
                rt.DrawText("WIN: " + wins, fmtLbl, new SharpDX.RectangleF(px, py, 90f, 15f), brVerde);
                rt.DrawText("LOSS: " + losses, fmtLbl, new SharpDX.RectangleF(px + 90f, py, 90f, 15f), brVermelho);
                rt.DrawText("Taxa: " + taxa.ToString("0") + "%", fmtLbl, new SharpDX.RectangleF(px + 180f, py, 110f, 15f), brAmarelo);
                py += 20f;
                rt.DrawText("(LOSS = candle seguinte rompeu " + PontosLossReverso.ToString("0") + " pts o pavio)", fmtSmall, new SharpDX.RectangleF(px, py, larg - 24f, 13f), brCinza);
                py += 18f;

                using (var brSep = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color(80, 90, 105, 200)))
                    rt.DrawLine(new SharpDX.Vector2(px, py), new SharpDX.Vector2(x + larg - 12f, py), brSep, 1f);
                py += 4f;
                rt.DrawText("ÚLTIMOS SINAIS (parâmetros acatados)", fmtSmall, new SharpDX.RectangleF(px, py, larg - 24f, 13f), brBarra);
                py += 15f;

                for (int i = _registrosSinais.Count - 1; i >= 0 && i >= _registrosSinais.Count - nLista; i--)
                {
                    var r = _registrosSinais[i];
                    var brDir = r.venda ? brVermelho : brVerde;
                    string dir = r.venda ? "▼V" : "▲C";
                    string tipo = r.completo ? "COMPL" : "parc";
                    string res = r.resultado == 1 ? "WIN" : (r.resultado == -1 ? "LOSS" : "…");
                    var brRes = r.resultado == 1 ? brVerde : (r.resultado == -1 ? brVermelho : brCinza);
                    rt.DrawText(r.hora.ToString("HH:mm") + " " + dir + " [" + r.estrategia + "] " + tipo,
                        fmtSmall, new SharpDX.RectangleF(px, py, 210f, 12f), brDir);
                    rt.DrawText(res, fmtSmall, new SharpDX.RectangleF(px + 215f, py, 70f, 12f), brRes);
                    py += 12f;
                    string cf = string.IsNullOrEmpty(r.confluencias) ? "—" : r.confluencias;
                    rt.DrawText(cf, fmtSmall, new SharpDX.RectangleF(px + 6f, py, larg - 30f, 12f), brCinza);
                    py += 20f;
                }
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || chartControl == null || chartScale == null) return;

            base.OnRender(chartControl, chartScale);

            if (MostrarZonasSD && Bars != null && Zones.Count > 0)
            {
                SharpDX.Direct2D1.AntialiasMode oldAntialiasMode = RenderTarget.AntialiasMode;
                RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
                
                SharpDX.Direct2D1.SolidColorBrush dBrush = GetDxBrush(RenderTarget, demandColor);
                SharpDX.Direct2D1.SolidColorBrush sBrush = GetDxBrush(RenderTarget, supplyColor);
                
                int wd = (int)(chartControl.BarWidth / 2.0) + (int)(chartControl.BarMarginLeft / 2.0);
                
                foreach(Zone z in ZonasEmUso)
                {
                    if(!z.a) continue;
                    
                    int x1 = ChartControl.GetXByBarIndex(ChartBars, z.b);
                    int x2 = (int)(ChartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex) + wd);
                    if(x2 < x1) continue;
                    
                    int y1 = chartScale.GetYByValue(z.h);
                    int y2 = chartScale.GetYByValue(z.l);
                    
                    SharpDX.RectangleF rect = new SharpDX.RectangleF((float)x1, (float)y1, (float)Math.Abs(x2 - x1), (float)Math.Abs(y1 - y2) - 1);
                    
                    dBrush.Opacity = activeAreaOpacity;
                    sBrush.Opacity = activeAreaOpacity;
                    
                    if(z.t == "d") RenderTarget.FillRectangle(rect, dBrush);
                    if(z.t == "s") RenderTarget.FillRectangle(rect, sBrush);
                    
                    dBrush.Opacity = activeLineOpacity;
                    sBrush.Opacity = activeLineOpacity;
                    
                    SharpDX.Vector2 p1 = new SharpDX.Vector2((float)x1, (float)y1);
                    SharpDX.Vector2 p2 = new SharpDX.Vector2((float)x2, (float)y1);
                    if(z.t == "d") RenderTarget.DrawLine(p1, p2, dBrush, 1f);
                    if(z.t == "s") RenderTarget.DrawLine(p1, p2, sBrush, 1f);
                    
                    p1.Y = (float)y2; p2.Y = (float)y2;
                    if(z.t == "d") RenderTarget.DrawLine(p1, p2, dBrush, 1f);
                    if(z.t == "s") RenderTarget.DrawLine(p1, p2, sBrush, 1f);
                }
                
                RenderTarget.AntialiasMode = oldAntialiasMode;
                dBrush.Dispose();
                sBrush.Dispose();
            }

            // ── SINAIS (setas/bolinhas) via SharpDX — render confiável no histórico ──
            // Contorna o Draw.* que não renderiza objetos criados em State.Historical.
            if (_marcas.Count > 0 && Bars != null && ChartBars != null)
            {
                try
                {
                    var oldAA = RenderTarget.AntialiasMode;
                    RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
                    int firstIdx = ChartBars.FromIndex, lastIdx = ChartBars.ToIndex;

                    // ── DESENHO DO VOLUME PROFILE (POC vermelho · VAH/VAL amarelo) ──
                    if (DesenharVolumeProfile)
                    {
                        try
                        {
                            float xIni = ChartControl.GetXByBarIndex(ChartBars, firstIdx);
                            float xFim = ChartControl.GetXByBarIndex(ChartBars, lastIdx);
                            using (var brPOC = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(230, 40, 40, 235)))
                            using (var brVA = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(235, 205, 40, 220)))
                            {
                                System.Action<double, SharpDX.Direct2D1.SolidColorBrush, float> linha = (preco, br, esp) =>
                                {
                                    if (preco <= 0) return;
                                    float y = chartScale.GetYByValue(preco);
                                    RenderTarget.DrawLine(new SharpDX.Vector2(xIni, y), new SharpDX.Vector2(xFim, y), br, esp);
                                };
                                // range de consolidação
                                if (_vpValido)
                                {
                                    linha(_vpPOC, brPOC, 1.6f);
                                    linha(_vpVAH, brVA, 1.2f);
                                    linha(_vpVAL, brVA, 1.2f);
                                }
                                // zonas ativas (pivôs / S&R / supply & demand)
                                if (VpNasZonas)
                                {
                                    foreach (var z in ZonasEmUso)
                                    {
                                        if (!z.a) continue;
                                        linha(z.poc, brPOC, 1.4f);
                                        linha(z.vah, brVA, 1.0f);
                                        linha(z.val, brVA, 1.0f);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // cor do sinal parcial: agora direcional — reaproveita as mesmas cores
                    // de venda/compra dos sinais completos (configuráveis pelo usuário),
                    // só com opacidade menor pra distinguir "parcial" de "completo" no gráfico.
                    SharpDX.Color dxParcialVenda = new SharpDX.Color(_corVendaR, _corVendaG, _corVendaB, (byte)170);
                    SharpDX.Color dxParcialCompra = new SharpDX.Color(_corCompraR, _corCompraG, _corCompraB, (byte)170);
                    // cor do sinal cancelado (modo conservador): configurável pelo usuário
                    SharpDX.Color dxCorCanc = new SharpDX.Color(_corCancR, _corCancG, _corCancB, (byte)200);
                    // cores dos sinais COMPLETOS (venda/compra): configuráveis pelo usuário
                    SharpDX.Color dxVenda = new SharpDX.Color(_corVendaR, _corVendaG, _corVendaB, (byte)255);
                    SharpDX.Color dxCompra = new SharpDX.Color(_corCompraR, _corCompraG, _corCompraB, (byte)255);
                    SharpDX.Color dxVendaH = new SharpDX.Color(_corVendaR, _corVendaG, _corVendaB, (byte)220);
                    SharpDX.Color dxCompraH = new SharpDX.Color(_corCompraR, _corCompraG, _corCompraB, (byte)220);
                    using (var brVendaSeta = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxVenda))
                    using (var brCompraSeta = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxCompra))
                    using (var brVendaHist = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxVendaH))
                    using (var brCompraHist = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxCompraH))
                    using (var brCancelado = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxCorCanc))
                    using (var brParcialVenda = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxParcialVenda))
                    using (var brParcialCompra = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, dxParcialCompra))
                    {
                        foreach (var m in _marcas)
                        {
                            if (m.bar < firstIdx - 2 || m.bar > lastIdx + 2) continue;   // fora da viewport
                            if (m.bar < 0 || m.bar > Bars.Count - 1) continue;

                            float x = ChartControl.GetXByBarIndex(ChartBars, m.bar);
                            // usa o high/low CONGELADO no momento em que o sinal nasceu — nunca
                            // reconsulta Bars.GetHigh/GetLow aqui, senão o sinal "anda" junto
                            // com o candle enquanto ele ainda está se formando.
                            double high = m.high, low = m.low;
                            double off = TickSize * 6;
                            double preco = m.venda ? high + off : low - off;
                            // marca da 2.0 no modo "usar ambas": desloca mais para fora
                            // (logo atrás/além da marca da 1.0), evitando sobreposição.
                            if (m.offset20) preco = m.venda ? high + off * 2.2 : low - off * 2.2;
                            float y = chartScale.GetYByValue(preco);

                            // cores: cancelado → cinza · parcial → vermelho(venda)/verde(compra) · completo → verde/vermelho
                            var br = m.cancelado ? brCancelado
                                   : m.parcial ? (m.venda ? brParcialVenda : brParcialCompra)
                                   : (m.venda ? (m.hist ? brVendaHist : brVendaSeta)
                                              : (m.hist ? brCompraHist : brCompraSeta));

                            if (m.formato == TipoSinal.Bolinha)
                            {
                                // BOLINHA
                                RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), 4.5f, 4.5f), br);
                            }
                            else if (m.formato == TipoSinal.Seta)
                            {
                                // SETA (linha + cabeça triangular)
                                float w = 5f, hh = 11f;
                                using (var geo = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
                                {
                                    using (var sk = geo.Open())
                                    {
                                        if (m.venda)
                                        {
                                            // aponta para baixo
                                            sk.BeginFigure(new SharpDX.Vector2(x - w, y - hh), SharpDX.Direct2D1.FigureBegin.Filled);
                                            sk.AddLine(new SharpDX.Vector2(x + w, y - hh));
                                            sk.AddLine(new SharpDX.Vector2(x + w, y - hh*0.4f));
                                            sk.AddLine(new SharpDX.Vector2(x + w*1.8f, y - hh*0.4f));
                                            sk.AddLine(new SharpDX.Vector2(x, y));
                                            sk.AddLine(new SharpDX.Vector2(x - w*1.8f, y - hh*0.4f));
                                            sk.AddLine(new SharpDX.Vector2(x - w, y - hh*0.4f));
                                        }
                                        else
                                        {
                                            // aponta para cima
                                            sk.BeginFigure(new SharpDX.Vector2(x - w, y + hh), SharpDX.Direct2D1.FigureBegin.Filled);
                                            sk.AddLine(new SharpDX.Vector2(x + w, y + hh));
                                            sk.AddLine(new SharpDX.Vector2(x + w, y + hh*0.4f));
                                            sk.AddLine(new SharpDX.Vector2(x + w*1.8f, y + hh*0.4f));
                                            sk.AddLine(new SharpDX.Vector2(x, y));
                                            sk.AddLine(new SharpDX.Vector2(x - w*1.8f, y + hh*0.4f));
                                            sk.AddLine(new SharpDX.Vector2(x - w, y + hh*0.4f));
                                        }
                                        sk.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                                        sk.Close();
                                    }
                                    RenderTarget.FillGeometry(geo, br);
                                }
                            }
                            else if (m.formato == TipoSinal.Diamante)
                            {
                                // DIAMANTE / LOSANGO (◆)
                                float r = 6.5f;
                                using (var geo = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
                                {
                                    using (var sk = geo.Open())
                                    {
                                        sk.BeginFigure(new SharpDX.Vector2(x, y - r), SharpDX.Direct2D1.FigureBegin.Filled);
                                        sk.AddLine(new SharpDX.Vector2(x + r, y));
                                        sk.AddLine(new SharpDX.Vector2(x, y + r));
                                        sk.AddLine(new SharpDX.Vector2(x - r, y));
                                        sk.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                                        sk.Close();
                                    }
                                    RenderTarget.FillGeometry(geo, br);
                                }
                            }
                            else if (m.formato == TipoSinal.Quadrado)
                            {
                                // QUADRADO (■)
                                float r = 5.5f;
                                RenderTarget.FillRectangle(new SharpDX.RectangleF(x - r, y - r, r * 2, r * 2), br);
                            }
                            else if (m.formato == TipoSinal.Cruz)
                            {
                                // CRUZ / X (✕)
                                float r = 6f, t = 2.2f;
                                using (var geo = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
                                {
                                    using (var sk = geo.Open())
                                    {
                                        // duas barras diagonais formando um X
                                        sk.BeginFigure(new SharpDX.Vector2(x - r, y - r + t), SharpDX.Direct2D1.FigureBegin.Filled);
                                        sk.AddLine(new SharpDX.Vector2(x - r + t, y - r));
                                        sk.AddLine(new SharpDX.Vector2(x, y - t));
                                        sk.AddLine(new SharpDX.Vector2(x + r - t, y - r));
                                        sk.AddLine(new SharpDX.Vector2(x + r, y - r + t));
                                        sk.AddLine(new SharpDX.Vector2(x + t, y));
                                        sk.AddLine(new SharpDX.Vector2(x + r, y + r - t));
                                        sk.AddLine(new SharpDX.Vector2(x + r - t, y + r));
                                        sk.AddLine(new SharpDX.Vector2(x, y + t));
                                        sk.AddLine(new SharpDX.Vector2(x - r + t, y + r));
                                        sk.AddLine(new SharpDX.Vector2(x - r, y + r - t));
                                        sk.AddLine(new SharpDX.Vector2(x - t, y));
                                        sk.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                                        sk.Close();
                                    }
                                    RenderTarget.FillGeometry(geo, br);
                                }
                            }
                            else if (m.formato == TipoSinal.Estrela)
                            {
                                // ESTRELA de 5 pontas (★)
                                float rOut = 7.5f, rIn = 3.1f;
                                using (var geo = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
                                {
                                    using (var sk = geo.Open())
                                    {
                                        for (int p = 0; p < 10; p++)
                                        {
                                            double ang = -Math.PI / 2 + p * Math.PI / 5;
                                            float rr = (p % 2 == 0) ? rOut : rIn;
                                            var pt = new SharpDX.Vector2(x + (float)(Math.Cos(ang) * rr), y + (float)(Math.Sin(ang) * rr));
                                            if (p == 0) sk.BeginFigure(pt, SharpDX.Direct2D1.FigureBegin.Filled);
                                            else sk.AddLine(pt);
                                        }
                                        sk.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                                        sk.Close();
                                    }
                                    RenderTarget.FillGeometry(geo, br);
                                }
                            }
                            else
                            {
                                // TRIÂNGULO (padrão)
                                float w = 6f, h = 9f;
                                using (var geo = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
                                {
                                    using (var sk = geo.Open())
                                    {
                                        if (m.venda)
                                        {
                                            sk.BeginFigure(new SharpDX.Vector2(x - w, y - h), SharpDX.Direct2D1.FigureBegin.Filled);
                                            sk.AddLine(new SharpDX.Vector2(x + w, y - h));
                                            sk.AddLine(new SharpDX.Vector2(x, y));
                                        }
                                        else
                                        {
                                            sk.BeginFigure(new SharpDX.Vector2(x - w, y + h), SharpDX.Direct2D1.FigureBegin.Filled);
                                            sk.AddLine(new SharpDX.Vector2(x + w, y + h));
                                            sk.AddLine(new SharpDX.Vector2(x, y));
                                        }
                                        sk.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                                        sk.Close();
                                    }
                                    RenderTarget.FillGeometry(geo, br);
                                }
                            }
                        }
                    }
                    RenderTarget.AntialiasMode = oldAA;
                }
                catch { }
            }

            // ── MINI DASHBOARD ESTATÍSTICO ──
            if (MostrarPainelEstatistico)
            {
                try { DesenharPainelEstatistico(chartControl, chartScale); } catch { }
            }

            // ── MARCA D'ÁGUA DA ESTRATÉGIA ATIVA (canto inferior direito) ──
            if (MostrarMarcaEstrategia)
            {
                try { DesenharMarcaEstrategia(); } catch { }
            }

            // O dashboard do gráfico só é desenhado se a janela flutuante NÃO estiver
            // ativa. Se o usuário escolheu a flutuante, apenas ela fica visível.
            if (MostrarDashboard && engine != null && !UsarPainelFlutuante && _estado.DashboardVisivel)
            {
                engine.Render(RenderTarget, PainelDeslocamentoX, PainelDeslocamentoY, _mouseX, _mouseY);
            }
        }
        #endregion

        #region --- ENGINE DO DASHBOARD INSTITUCIONAL HFT (IMAGE REDESIGN) ---

        public class DashboardMetrics
        {
            public double ConfidenceTarget { get; set; } = 0.0;
            public double ConfidenceCurrent { get; set; } = 0.0;
            public string AIInterpretation { get; set; } = "Aguardando atualizações de mercado...";
            public string Direction { get; set; } = "Neutro";
            public string Bias { get; set; } = "NEUTRO";
            public string FlowStr { get; set; } = "NEUTRO";
            public string SignalStr { get; set; } = "AGUARDANDO";
            public bool InSDZone { get; set; } = false;
            public double CurrentDelta { get; set; } = 0.0;
            public double PrecoAtual { get; set; } = 0.0;
            // Buffer dos últimos preços de fechamento (para o mini-gráfico de preço)
            public double[] PrecoHistorico { get; set; } = new double[0];
            public double PrecoVariacao { get; set; } = 0.0;   // variação absoluta (últimos ~2min)
            public double PrecoVariacaoPct { get; set; } = 0.0; // variação %

            // ── CAMADA 1: contexto/qualidade de mercado (para o painel) ──
            public bool   CtxAtivo { get; set; } = false;
            public string CtxRegime { get; set; } = "\u2014";          // "Tendencial"...
            public int    CtxEstrelas { get; set; } = 0;               // 1..5
            public string CtxQualidadeTxt { get; set; } = "\u2014";    // "Bom"...
            public bool   CtxPodeOperar { get; set; } = true;
            public string CtxMotivoBloqueio { get; set; } = "";
            public double CtxScoreTendencia { get; set; } = 0;
            public double CtxScoreVolatilidade { get; set; } = 0;
            public double CtxScoreForca { get; set; } = 0;
            public double CtxScoreLiquidez { get; set; } = 0;
            public double CtxAdx { get; set; } = 0;

            // ── ETAPA 1: delta real tick a tick (para painel e gates futuros) ──
            public double DeltaReal { get; set; } = 0;       // delta da barra corrente
            public double DeltaMin { get; set; } = 0;        // extremo mínimo intra-barra
            public double DeltaMax { get; set; } = 0;        // extremo máximo intra-barra
            public bool   DeltaRealAtivo { get; set; } = false; // true quando a fonte é tick a tick

            // ── ETAPA 2: zonas Dipcorp (fractais 5m+1m) ──
            public bool DipcorpAtivo { get; set; } = false;      // Dipcorp compilado e disponível
            public int  DipcorpConfluenciaZona { get; set; } = 0; // grau de confluência no preço atual

            // ── ETAPA 3: gate FLIP + R:R ──
            public int    FlipDirecao { get; set; } = 0;   // +1 compra, -1 venda, 0 nenhum
            public double FlipStopPts { get; set; } = 0;   // distância do stop (pontos)
            public double FlipRR { get; set; } = 0;        // razão risco/retorno até a próxima zona

            // ── ETAPA 4: macro60, ExR ──
            public bool Bull60 { get; set; } = false;      // tendência de alta no 60m
            public bool Bear60 { get; set; } = false;      // tendência de baixa no 60m
            public int  ExrEstado { get; set; } = 0;       // 0 neutro, 1 absorção, 2 resultado
            public bool IsSignalActive { get; set; } = false;
            // Pontuação real de proximidade/qualidade da zona de Supply & Demand (0-100), calculada a partir do preço atual.
            public double ZoneScore { get; set; } = 20.0;
            // Tipo de zona atual por confluência com Supply & Demand: "SUPPLY"/"DEMAND"/"NONE"
            public string ZoneType { get; set; } = "NONE";
            public string Confluencias { get; set; } = "";

            // ── SISTEMA DE PRÉ-SINAL INTELIGENTE ──
            // Estados: AGUARDANDO, POSSIVEL_COMPRA, POSSIVEL_VENDA, COMPRA, VENDA, CANCELADO
            public string EstadoSinal { get; set; } = "AGUARDANDO";
            public string CardTexto { get; set; } = "AGUARDANDO";
            public int    CardTipo { get; set; } = 0;
            public string IaMensagem { get; set; } = "";
            public double ProgressoConfirmacao { get; set; } = 0.0; // 0-100 durante os 10s
            public double SignalScore { get; set; } = 0.0;          // score geral 0-100
            public string[] IABullets { get; set; } = new string[0]; // bullets da análise IA
            // Valores individuais dos fatores (para as barras de indicadores)
            public double FluxoPct { get; set; } = 0;
            public double MomentumPct { get; set; } = 0;
            public double VolumePct { get; set; } = 0;
            public double TendenciaPct { get; set; } = 0;
            public double VolatilidadePct { get; set; } = 0;
            public double ConfluenciaPct { get; set; } = 0;
            // Timestamp de início da confirmação (para progresso em tempo real no render)
            public DateTime PreSinalInicio { get; set; } = DateTime.MinValue;
            public double PreSinalSegundos { get; set; } = 10.0;
        }

        public class DashboardTheme
        {
            // ═══════════════════════════════════════════════════════════════════════════
            // RÉPLICA EXATA — Profit Academy Dashboard (referência visual)
            // ═══════════════════════════════════════════════════════════════════════════
            
            // SUPERFÍCIES — preto profundo com cards levemente elevados
            public SharpDX.Color BgMain { get; } = new SharpDX.Color(11, 14, 19);           // #0B0E13 fundo institucional
            public SharpDX.Color PanelBg { get; } = new SharpDX.Color(16, 21, 28);          // #10151C cards
            public SharpDX.Color CardBg { get; } = new SharpDX.Color(16, 21, 28, 255);      // #10151C
            public SharpDX.Color CardElement { get; } = new SharpDX.Color(22, 28, 36);      // #161C24 elementos
            public SharpDX.Color CardBorder { get; } = new SharpDX.Color(36, 43, 52);       // #242B34 bordas
            public SharpDX.Color BorderColor { get; } = new SharpDX.Color(36, 43, 52, 255); // #242B34
            
            // TIPOGRAFIA
            public SharpDX.Color TextPrimary { get; } = new SharpDX.Color(245, 247, 250);   // #F5F7FA branco
            public SharpDX.Color TextSecondary { get; } = new SharpDX.Color(141, 153, 168); // #8D99A8 cinza
            public SharpDX.Color TextDisabled { get; } = new SharpDX.Color(80, 88, 98);     // cinza escuro
            
            // ACCENTS — paleta institucional restrita
            public SharpDX.Color Green { get; } = new SharpDX.Color(34, 197, 94);           // #22C55E BUY
            public SharpDX.Color DeepGreen { get; } = new SharpDX.Color(28, 165, 78);
            public SharpDX.Color Red { get; } = new SharpDX.Color(239, 68, 68);             // #EF4444 SELL
            public SharpDX.Color DeepRed { get; } = new SharpDX.Color(200, 55, 55);
            public SharpDX.Color Blue { get; } = new SharpDX.Color(74, 144, 226);           // #4A90E2 INFORMATION
            public SharpDX.Color Gold { get; } = new SharpDX.Color(212, 175, 55);           // #D4AF37 WARNING
            public SharpDX.Color Yellow { get; } = new SharpDX.Color(212, 175, 55);         // #D4AF37
            public SharpDX.Color White { get; } = new SharpDX.Color(245, 247, 250);
            
            // GLOWS — sutis para o gauge (a imagem tem glow no anel)
            public SharpDX.Color GlowRed { get; } = new SharpDX.Color(255, 61, 61, 60);
            public SharpDX.Color GlowGreen { get; } = new SharpDX.Color(34, 217, 94, 60);
            public SharpDX.Color GlowGold { get; } = new SharpDX.Color(230, 170, 70, 0);
            public SharpDX.Color GlowBlue { get; } = new SharpDX.Color(120, 130, 145, 0);
        }

        public class DashboardResources : IDisposable
        {
            public SharpDX.Direct2D1.SolidColorBrush BrushBgMain;
            public SharpDX.Direct2D1.SolidColorBrush BrushPanelBg;
            public SharpDX.Direct2D1.SolidColorBrush BrushCardBg;
            public SharpDX.Direct2D1.SolidColorBrush BrushBorder;
            public SharpDX.Direct2D1.SolidColorBrush BrushCardElement;
            public SharpDX.Direct2D1.SolidColorBrush BrushTextDisabled;
            
            public SharpDX.Direct2D1.SolidColorBrush BrushTextPrimary;
            public SharpDX.Direct2D1.SolidColorBrush BrushTextSecondary;

            public SharpDX.Direct2D1.SolidColorBrush BrushGreen;
            public SharpDX.Direct2D1.SolidColorBrush BrushDeepGreen;
            public SharpDX.Direct2D1.SolidColorBrush BrushRed;
            public SharpDX.Direct2D1.SolidColorBrush BrushYellow;
            public SharpDX.Direct2D1.SolidColorBrush BrushBlue;
            public SharpDX.Direct2D1.SolidColorBrush BrushWhite;
            public SharpDX.Direct2D1.SolidColorBrush BrushGray;
            public SharpDX.Direct2D1.SolidColorBrush BrushGold;
            
            public SharpDX.Direct2D1.SolidColorBrush BrushGreenBadge;
            public SharpDX.Direct2D1.SolidColorBrush BrushRedBadge;
            public SharpDX.Direct2D1.SolidColorBrush BrushGoldBadge;
            // Brushes refinados para o painel BASIC (tons mais foscos/premium)
            public SharpDX.Direct2D1.SolidColorBrush BrushGreenSolido;   // verde fosco preenchido
            public SharpDX.Direct2D1.SolidColorBrush BrushRedSolido;     // vermelho fosco preenchido
            public SharpDX.Direct2D1.SolidColorBrush BrushGoldFosco;     // dourado mostarda elegante
            public SharpDX.Direct2D1.SolidColorBrush BrushPillBg;        // fundo da pílula do toggle
            
            public SharpDX.Direct2D1.SolidColorBrush BrushGlowGreen;
            public SharpDX.Direct2D1.SolidColorBrush BrushGlowRed;
            public SharpDX.Direct2D1.SolidColorBrush BrushTopLight;
            
            // NOVO: Brushes premium para glows sofisticados
            public SharpDX.Direct2D1.SolidColorBrush BrushGoldGlow;
            public SharpDX.Direct2D1.SolidColorBrush BrushBlueGlow;

            public SharpDX.Direct2D1.StrokeStyle StrokeStyleRound;
            public SharpDX.Direct2D1.StrokeStyle StrokeStyleDash;

            public SharpDX.DirectWrite.TextFormat FontTitle;
            public SharpDX.DirectWrite.TextFormat FontLabel;
            public SharpDX.DirectWrite.TextFormat FontValue;
            public SharpDX.DirectWrite.TextFormat FontSignal;
            public SharpDX.DirectWrite.TextFormat FontSignalMed;
            public SharpDX.DirectWrite.TextFormat FontGaugeBig;
            public SharpDX.DirectWrite.TextFormat FontGaugeSmall;
            public SharpDX.DirectWrite.TextFormat FontIA;
            public SharpDX.DirectWrite.TextFormat FontCfgNome;
            public SharpDX.DirectWrite.TextFormat FontCfgHint;
            public SharpDX.DirectWrite.TextFormat FontSmall;
            public SharpDX.DirectWrite.TextFormat FontMonitor;   // MONITORANDO (negrito, maior)
            public SharpDX.DirectWrite.TextFormat FontBasicPct;
            public SharpDX.DirectWrite.TextFormat FontBasicSinal;

            public void CreateResources(RenderTarget renderTarget, DashboardTheme theme)
            {
                Dispose(); 

                BrushBgMain = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.BgMain);
                BrushPanelBg = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.PanelBg);
                BrushCardBg = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.CardBg);
                BrushBorder = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.BorderColor);
                BrushCardElement = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.CardElement);
                BrushTextDisabled = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.TextDisabled);
                
                BrushTextPrimary = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.TextPrimary);
                BrushTextSecondary = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.TextSecondary);

                BrushGreen = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.Green);
                BrushDeepGreen = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.DeepGreen);
                BrushRed = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.Red);
                BrushYellow = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.Yellow);
                BrushBlue = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.Blue);
                BrushWhite = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.White);
                BrushGray = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.TextSecondary);
                BrushGold = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.Yellow);

                BrushGreenBadge = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(theme.Green.R, theme.Green.G, theme.Green.B, (byte)38));
                BrushRedBadge = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(theme.Red.R, theme.Red.G, theme.Red.B, (byte)38));
                BrushGoldBadge = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(theme.Yellow.R, theme.Yellow.G, theme.Yellow.B, (byte)38));
                // Premium foscos — alinhados com paleta Bloomberg
                BrushGreenSolido = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color((byte)30, (byte)180, (byte)90, (byte)230));  // Emerald fosco
                BrushRedSolido = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color((byte)200, (byte)70, (byte)70, (byte)230));  // Soft Red fosco
                // Amber premium
                BrushGoldFosco = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color((byte)245, (byte)197, (byte)66, (byte)230));  // #F5C542
                // Pill bg — #222C3D
                BrushPillBg = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color((byte)34, (byte)44, (byte)61, (byte)255));

                BrushGlowGreen = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.GlowGreen);
                BrushGlowRed = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.GlowRed);
                BrushTopLight = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(255, 255, 255, 30));

                // NOVO: Brushes premium para glows sofisticados
                BrushGoldGlow = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.GlowGold);
                BrushBlueGlow = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, theme.GlowBlue);

                SharpDX.Direct2D1.StrokeStyleProperties strokeProps = new SharpDX.Direct2D1.StrokeStyleProperties()
                {
                    StartCap = SharpDX.Direct2D1.CapStyle.Round,
                    EndCap = SharpDX.Direct2D1.CapStyle.Round,
                    DashCap = SharpDX.Direct2D1.CapStyle.Round,
                    LineJoin = SharpDX.Direct2D1.LineJoin.Round
                };
                StrokeStyleRound = new SharpDX.Direct2D1.StrokeStyle(renderTarget.Factory, strokeProps);

                SharpDX.Direct2D1.StrokeStyleProperties dashProps = new SharpDX.Direct2D1.StrokeStyleProperties()
                {
                    StartCap = SharpDX.Direct2D1.CapStyle.Flat,
                    EndCap = SharpDX.Direct2D1.CapStyle.Flat,
                    DashCap = SharpDX.Direct2D1.CapStyle.Flat,
                    DashStyle = SharpDX.Direct2D1.DashStyle.Dash,
                };
                StrokeStyleDash = new SharpDX.Direct2D1.StrokeStyle(renderTarget.Factory, dashProps);

                SharpDX.DirectWrite.Factory dwFactory = NinjaTrader.Core.Globals.DirectWriteFactory;
                string fontFamily = "Segoe UI"; // Apple Design Language Approximation for Windows

                FontTitle = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 18f);
                FontLabel = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Medium, SharpDX.DirectWrite.FontStyle.Normal, 12f);
                FontValue = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.SemiBold, SharpDX.DirectWrite.FontStyle.Normal, 16f);
                
                FontSignal = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 42f);
                FontSignal.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                FontSignal.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;

                FontSignalMed = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 30f);
                FontSignalMed.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                FontSignalMed.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                
                FontGaugeBig = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 50f);
                FontGaugeBig.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                
                FontGaugeSmall = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Medium, SharpDX.DirectWrite.FontStyle.Normal, 12f);
                FontGaugeSmall.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;

                FontIA = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Regular, SharpDX.DirectWrite.FontStyle.Normal, 16f);
                // Fontes do modal de config — maiores para leitura confortável
                FontCfgNome = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.SemiBold, SharpDX.DirectWrite.FontStyle.Normal, 21f);
                FontCfgNome.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                FontCfgHint = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Regular, SharpDX.DirectWrite.FontStyle.Normal, 15.5f);
                FontCfgHint.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                FontSmall = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Light, SharpDX.DirectWrite.FontStyle.Normal, 10f);
                FontMonitor = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 13f);
                FontMonitor.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;

                FontBasicPct = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 30f);
                FontBasicPct.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                FontBasicPct.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                FontBasicSinal = new SharpDX.DirectWrite.TextFormat(dwFactory, fontFamily, SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 24f);
                FontBasicSinal.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                FontBasicSinal.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
            }

            public void Dispose()
            {
                BrushBgMain?.Dispose(); BrushPanelBg?.Dispose(); BrushCardBg?.Dispose(); BrushBorder?.Dispose();
                BrushCardElement?.Dispose(); BrushTextDisabled?.Dispose();
                BrushTextPrimary?.Dispose(); BrushTextSecondary?.Dispose();
                BrushGreen?.Dispose(); BrushDeepGreen?.Dispose(); BrushRed?.Dispose(); 
                BrushYellow?.Dispose(); BrushBlue?.Dispose(); BrushWhite?.Dispose(); BrushGray?.Dispose();
                BrushGold?.Dispose();
                BrushGreenBadge?.Dispose(); BrushRedBadge?.Dispose(); BrushGoldBadge?.Dispose();
                BrushGlowGreen?.Dispose(); BrushGlowRed?.Dispose(); BrushTopLight?.Dispose();
                StrokeStyleRound?.Dispose(); StrokeStyleDash?.Dispose();
                FontTitle?.Dispose(); FontLabel?.Dispose(); FontValue?.Dispose(); FontSignal?.Dispose();
                FontGaugeBig?.Dispose(); FontGaugeSmall?.Dispose(); FontIA?.Dispose(); FontSmall?.Dispose(); FontMonitor?.Dispose();
                FontCfgNome?.Dispose(); FontCfgHint?.Dispose();
            }
        }

        public class DashboardDraw
        {
            private RenderTarget renderTarget;
            private DashboardResources res;
            // ── estado do AI Core (transição de cor e escala) ──
            private float _aiR = 0.9f, _aiG = 0.92f, _aiB = 0.95f;
            private bool  _aiCorInit = false;
            private float _aiEscala = 1f;
            public bool BiasVendaAI = false;   // setado pelo engine antes de desenhar o core

            public DashboardDraw(RenderTarget rt, DashboardResources resources)
            {
                this.renderTarget = rt;
                this.res = resources;
            }

            public void DrawGlassCard(SharpDX.RectangleF rect, float mouseX, float mouseY, SharpDX.Direct2D1.SolidColorBrush glowBrush = null)
            {
                float radius = 12f;
                SharpDX.Direct2D1.RoundedRectangle rRect = new SharpDX.Direct2D1.RoundedRectangle { Rect = rect, RadiusX = radius, RadiusY = radius };
                // Fill de elevação sutil sobre a base
                renderTarget.FillRoundedRectangle(rRect, res.BrushCardBg);
                // Hairline border (branco 4%)
                renderTarget.DrawRoundedRectangle(rRect, res.BrushBorder, 1.0f);
            }

            public void DrawText(string text, SharpDX.DirectWrite.TextFormat font, SharpDX.RectangleF rect, SharpDX.Direct2D1.SolidColorBrush brush, SharpDX.DirectWrite.TextAlignment align = SharpDX.DirectWrite.TextAlignment.Leading)
            {
                font.TextAlignment = align;
                renderTarget.DrawText(text, font, rect, brush);
            }

            public void DrawBadge(SharpDX.RectangleF rect, string text, SharpDX.Direct2D1.SolidColorBrush textBrush, SharpDX.Direct2D1.SolidColorBrush bgBrush)
            {
                SharpDX.Direct2D1.RoundedRectangle rRect = new SharpDX.Direct2D1.RoundedRectangle { Rect = rect, RadiusX = 10f, RadiusY = 10f };
                renderTarget.FillRoundedRectangle(rRect, bgBrush);
                DrawText(text, res.FontValue, new SharpDX.RectangleF(rect.Left, rect.Top + 8, rect.Width, rect.Height), textBrush, SharpDX.DirectWrite.TextAlignment.Center);
            }

            public void DrawRingProgress(float x, float y, float w, float h, double percentage, string label, SharpDX.Direct2D1.SolidColorBrush ringColor, float mouseX, float mouseY)
            {
                // ═══════════════════════════════════════════════════════════════
                // AI CORE — núcleo de IA institucional (substitui o gauge circular).
                // Núcleo pulsante + 2 anéis rotativos + data nodes + partículas +
                // cores dinâmicas por score, transição suave, expansão ao atingir 85%.
                // ═══════════════════════════════════════════════════════════════
                float cx = x + w / 2f;
                float cy = y + h / 2f;
                float raio = Math.Min(w, h) / 2f - 16f;

                double pctv = Math.Max(0, Math.Min(100, percentage));
                float tAnim = Environment.TickCount / 1000f;

                // cor-alvo por faixa (RGB 0..1): branco→azul→dourado→verde/vermelho
                float tr, tg, tb;
                bool venda = BiasVendaAI;
                if (pctv >= 85)
                {
                    if (venda) { tr = 0.937f; tg = 0.267f; tb = 0.267f; } // #EF4444
                    else       { tr = 0.133f; tg = 0.773f; tb = 0.369f; } // #22C55E
                }
                else if (pctv >= 55) { tr = 0.831f; tg = 0.686f; tb = 0.216f; } // #D4AF37
                else if (pctv >= 40) { tr = 0.231f; tg = 0.510f; tb = 0.965f; } // #3B82F6
                else { tr = 0.90f; tg = 0.92f; tb = 0.95f; }                    // branco

                if (!_aiCorInit) { _aiR = tr; _aiG = tg; _aiB = tb; _aiCorInit = true; }
                float lerp = 0.12f;
                _aiR += (tr - _aiR) * lerp; _aiG += (tg - _aiG) * lerp; _aiB += (tb - _aiB) * lerp;

                var core = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color4(_aiR, _aiG, _aiB, 1f));

                float intensidade = (float)(pctv / 100.0);
                bool sinalPronto = pctv >= 85;
                float baseFreq = 1.0f + intensidade * 1.6f;
                float pulso = (float)(0.5 + 0.5 * Math.Sin(tAnim * baseFreq));
                float batida2s = 0f;
                if (sinalPronto) { float ph = (tAnim % 2f) / 2f; if (ph < 0.25f) batida2s = (float)Math.Sin(ph / 0.25f * Math.PI) * 0.5f; }

                float escalaAlvo = sinalPronto ? 1.05f : 1.0f;
                _aiEscala += (escalaAlvo - _aiEscala) * 0.1f;
                float raioE = raio * _aiEscala;

                try
                {
                    // HALO / glow (some abaixo de 55%)
                    float glowBase = pctv >= 55 ? 0.06f : 0.03f;
                    float glowAlpha = (glowBase + 0.12f * intensidade) * (1f + batida2s);
                    for (int g = 3; g >= 1; g--)
                    {
                        float gr = raioE + 8f + g * 7f + pulso * 4f;
                        core.Opacity = glowAlpha / g;
                        renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), gr, gr), core, 2f);
                    }

                    // DATA NODES: trilha circular fina + nós piscando
                    core.Opacity = 0.12f + 0.10f * intensidade;
                    renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), raioE + 12f, raioE + 12f), core, 0.8f);
                    int nNodes = 6;
                    for (int n = 0; n < nNodes; n++)
                    {
                        float na = (360f / nNodes * n + tAnim * 8f) * (float)Math.PI / 180f;
                        float nx = cx + (float)Math.Cos(na) * (raioE + 12f);
                        float ny = cy + (float)Math.Sin(na) * (raioE + 12f);
                        float nblink = (float)(0.3 + 0.7 * Math.Abs(Math.Sin(tAnim * 1.5 + n * 1.3)));
                        core.Opacity = (0.3f + 0.4f * intensidade) * nblink;
                        renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(nx, ny), 2.2f, 2.2f), core);
                    }

                    // OUTER RING (anti-horário)
                    DesenharAnelCore(cx, cy, raioE + 4f, -tAnim * 25f, 220f, core, 1.8f, 0.55f + (sinalPronto ? 0.3f + batida2s : 0f));
                    // INNER RING (horário)
                    DesenharAnelCore(cx, cy, raioE - 6f, tAnim * 40f, 260f, core, 1.5f, 0.78f);

                    // MICRO PARTÍCULAS
                    int nPart = 8;
                    float velExtra = sinalPronto ? 10f : 0f;
                    for (int p = 0; p < nPart; p++)
                    {
                        float baseAng = (360f / nPart) * p + (p * 13 % 40);
                        float vel = 16f + (p % 3) * 12f + velExtra;
                        float ang = (baseAng + tAnim * vel) * (float)Math.PI / 180f;
                        float orb = raioE + 4f + (p % 2 == 0 ? 6f : -2f);
                        float px = cx + (float)Math.Cos(ang) * orb;
                        float py = cy + (float)Math.Sin(ang) * orb;
                        float blink = (float)(0.4 + 0.6 * Math.Sin(tAnim * 2.0 + p));
                        core.Opacity = (0.35f + 0.5f * intensidade) * blink;
                        float pr = 1.6f + intensidade * 1.4f + (sinalPronto ? batida2s * 1.5f : 0f);
                        renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(px, py), pr, pr), core);
                    }

                    // NÚCLEO central pulsante
                    float raioNucleo = (raio * 0.5f + pulso * (3f + intensidade * 4f)) * _aiEscala;
                    core.Opacity = 0.10f + 0.16f * intensidade + batida2s * 0.1f;
                    renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), raioNucleo, raioNucleo), core);
                    core.Opacity = 0.6f + 0.3f * intensidade;
                    renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), raioNucleo, raioNucleo), core, 1f);

                    // RIPPLE a cada 5s
                    float ripplePhase = (tAnim % 5f) / 5f;
                    if (ripplePhase < 0.6f)
                    {
                        float rr2 = raioE * (0.4f + ripplePhase * 1.3f);
                        core.Opacity = 0.25f * (1f - ripplePhase / 0.6f);
                        renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), rr2, rr2), core, 1.2f);
                    }

                    // Texto central: número estético (não é score) + labels de monitoramento
                    core.Opacity = 1f;
                    DrawText(pctv.ToString("F0"), res.FontGaugeBig, new SharpDX.RectangleF(x, cy - 40, w, 60), core, SharpDX.DirectWrite.TextAlignment.Center);
                    string aiSub = pctv >= 55 ? "FLOW ATIVO"
                                 : pctv >= 35 ? "MONITORANDO"
                                 : "ANALISANDO";
                    DrawText(aiSub, res.FontGaugeSmall, new SharpDX.RectangleF(x, cy + 20, w, 15), core, SharpDX.DirectWrite.TextAlignment.Center);
                    DrawText("Fluxo de Mercado", res.FontSmall, new SharpDX.RectangleF(x, cy + 37, w, 13), res.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                }
                catch { }
                finally { core.Dispose(); }
            }

            // Anel parcial rotacionado para o AI Core (helper interno do engine).
            private void DesenharAnelCore(float cx, float cy, float raio, float startDeg, float sweepDeg, SharpDX.Direct2D1.SolidColorBrush cor, float stroke, float alpha)
            {
                try
                {
                    float radS = startDeg * (float)Math.PI / 180f;
                    float radE = (startDeg + sweepDeg) * (float)Math.PI / 180f;
                    var p1 = new SharpDX.Vector2(cx + (float)Math.Cos(radS) * raio, cy + (float)Math.Sin(radS) * raio);
                    var p2 = new SharpDX.Vector2(cx + (float)Math.Cos(radE) * raio, cy + (float)Math.Sin(radE) * raio);
                    float o = cor.Opacity; cor.Opacity = alpha;
                    using (var arcGeo = new SharpDX.Direct2D1.PathGeometry(renderTarget.Factory))
                    {
                        using (var sink = arcGeo.Open())
                        {
                            sink.BeginFigure(p1, SharpDX.Direct2D1.FigureBegin.Hollow);
                            sink.AddArc(new SharpDX.Direct2D1.ArcSegment { Point = p2, Size = new SharpDX.Size2F(raio, raio), RotationAngle = 0, SweepDirection = SharpDX.Direct2D1.SweepDirection.Clockwise, ArcSize = sweepDeg > 180 ? SharpDX.Direct2D1.ArcSize.Large : SharpDX.Direct2D1.ArcSize.Small });
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
                            sink.Close();
                        }
                        renderTarget.DrawGeometry(arcGeo, cor, stroke);
                    }
                    cor.Opacity = o;
                }
                catch { }
            }

            public void DrawTechIndicator(float x, float y, float w, float h, string label, string statusStr, string percStr, SharpDX.Direct2D1.SolidColorBrush mainColor, int intensity, string iconType, float mouseX, float mouseY)
            {
                SharpDX.RectangleF rect = new SharpDX.RectangleF(x, y, w, h);

                // Zero glow — ultra premium = sem efeitos luminosos

                DrawGlassCard(rect, mouseX, mouseY);

                // Borda fina na cor do estado (apenas quando ativo)
                {
                    float savedBOp = mainColor.Opacity;
                    mainColor.Opacity = 0.15f;
                    SharpDX.Direct2D1.RoundedRectangle rBorder = new SharpDX.Direct2D1.RoundedRectangle { Rect = rect, RadiusX = 18f, RadiusY = 18f };
                    renderTarget.DrawRoundedRectangle(rBorder, mainColor, 1f);
                    mainColor.Opacity = savedBOp;
                }

                bool isHovered = rect.Contains(mouseX, mouseY);
                if (isHovered) renderTarget.Transform = SharpDX.Matrix3x2.Scaling(1.02f, 1.02f, new SharpDX.Vector2(x + w / 2, y + h / 2));

                // Icon Box (Esquerda) — fundo levemente tingido com a cor do estado, dando destaque visual
                // sem virar ruído (como na referência: caixa quase preta com halo suave da cor do card).
                float boxS = 44f;
                SharpDX.RectangleF iconRect = new SharpDX.RectangleF(rect.Left + 20, rect.Top + (h - boxS)/2, boxS, boxS);
                SharpDX.Direct2D1.RoundedRectangle rIcon = new SharpDX.Direct2D1.RoundedRectangle { Rect = iconRect, RadiusX = 12f, RadiusY = 12f };
                renderTarget.FillRoundedRectangle(rIcon, res.BrushBgMain);

                float savedIconTint = mainColor.Opacity;
                mainColor.Opacity = 0.10f;
                renderTarget.FillRoundedRectangle(rIcon, mainColor);
                mainColor.Opacity = savedIconTint;

                mainColor.Opacity = 0.35f;
                renderTarget.DrawRoundedRectangle(rIcon, mainColor, 1f);
                mainColor.Opacity = savedIconTint;

                // Ícones vetoriais coloridos com a cor do estado (mainColor) — leitura instantânea.
                float icx = iconRect.Left + boxS / 2f;
                float icy = iconRect.Top  + boxS / 2f;
                if (iconType == "Estrutura")
                {
                    // Camadas empilhadas (layers) — como no ícone da referência para "Estrutura"
                    var top    = new SharpDX.Vector2[] { new SharpDX.Vector2(icx, icy-11), new SharpDX.Vector2(icx+11, icy-5), new SharpDX.Vector2(icx, icy+1),  new SharpDX.Vector2(icx-11, icy-5) };
                    var bottom = new SharpDX.Vector2[] { new SharpDX.Vector2(icx, icy-4),  new SharpDX.Vector2(icx+11, icy+2), new SharpDX.Vector2(icx, icy+8),  new SharpDX.Vector2(icx-11, icy+2) };
                    for (int seg = 0; seg < 4; seg++)
                    {
                        renderTarget.DrawLine(top[seg], top[(seg+1)%4], mainColor, 1.8f);
                        renderTarget.DrawLine(bottom[seg], bottom[(seg+1)%4], mainColor, 1.8f);
                    }
                }
                else if (iconType == "Pressao")
                {
                    // Pulso ECG limpo e centralizado
                    renderTarget.DrawLine(new SharpDX.Vector2(icx-11, icy), new SharpDX.Vector2(icx-5, icy), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx-5, icy),  new SharpDX.Vector2(icx-2, icy-9), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx-2, icy-9),new SharpDX.Vector2(icx+2, icy+9), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx+2, icy+9),new SharpDX.Vector2(icx+5, icy), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx+5, icy),  new SharpDX.Vector2(icx+11, icy), mainColor, 2f);
                }
                else if (iconType == "Liquidez")
                {
                    // Gota d'água estilizada
                    renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(icx, icy+3), 7f, 7f), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx-7, icy+3), new SharpDX.Vector2(icx, icy-10), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx+7, icy+3), new SharpDX.Vector2(icx, icy-10), mainColor, 2f);
                }
                else if (iconType == "Supply")
                {
                    // Crosshair (mira)
                    renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(icx, icy), 7f, 7f), mainColor, 2f);
                    renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(icx, icy), 2f, 2f), mainColor);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx, icy-12), new SharpDX.Vector2(icx, icy-9), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx, icy+9), new SharpDX.Vector2(icx, icy+12), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx-12, icy), new SharpDX.Vector2(icx-9, icy), mainColor, 2f);
                    renderTarget.DrawLine(new SharpDX.Vector2(icx+9, icy), new SharpDX.Vector2(icx+12, icy), mainColor, 2f);
                }

                // Textos
                float textX = iconRect.Right + 18f;
                DrawText(label, res.FontLabel, new SharpDX.RectangleF(textX, rect.Top + 16, 180, 20), res.BrushTextSecondary);
                DrawText(statusStr, res.FontValue, new SharpDX.RectangleF(textX, rect.Top + 35, 180, 22), mainColor);

                // Porcentagem (Direita) — mesma tipografia grande do status
                DrawText(percStr, res.FontValue, new SharpDX.RectangleF(rect.Left, rect.Top + 24, rect.Width - 26, 22), mainColor, SharpDX.DirectWrite.TextAlignment.Trailing);

                // Progress bar segmentada — 10 segmentos finos, no estilo da referência
                float barX = textX;
                float barY = rect.Bottom - 20f;
                int segments = 10;
                float gap = 4f;
                float availW = rect.Width - (textX - rect.Left) - 26f;
                float segW = (availW - (gap * (segments - 1))) / segments;
                if (segW < 6f) segW = 6f;
                float segH = 5f;

                for (int i = 0; i < segments; i++)
                {
                    SharpDX.RectangleF sR = new SharpDX.RectangleF(barX + (i * (segW + gap)), barY, segW, segH);
                    SharpDX.Direct2D1.RoundedRectangle rsR = new SharpDX.Direct2D1.RoundedRectangle { Rect = sR, RadiusX = 2.5f, RadiusY = 2.5f };

                    if (i < intensity)
                    {
                        // Halo suave do segmento aceso
                        float savedSegOp = mainColor.Opacity;
                        mainColor.Opacity = 0.25f;
                        SharpDX.RectangleF halo = new SharpDX.RectangleF(sR.Left - 1.5f, sR.Top - 1.5f, sR.Width + 3f, sR.Height + 3f);
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = halo, RadiusX = 3.5f, RadiusY = 3.5f }, mainColor);
                        mainColor.Opacity = savedSegOp;

                        renderTarget.FillRoundedRectangle(rsR, mainColor);
                    }
                    else
                    {
                        // Segmento apagado: bem sutil, quase imperceptível (trilho de fundo)
                        float savedOffOp = res.BrushTextSecondary.Opacity;
                        res.BrushTextSecondary.Opacity = 0.12f;
                        renderTarget.FillRoundedRectangle(rsR, res.BrushTextSecondary);
                        res.BrushTextSecondary.Opacity = savedOffOp;
                    }
                }

                if (isHovered) renderTarget.Transform = SharpDX.Matrix3x2.Identity;
            }
        }

        public class DashboardAnimations
        {
            public double Lerp(double start, double end, double amount)
            {
                return start + (end - start) * amount;
            }
        }

        public class DashboardEngine : IDisposable
        {
            // Estado de configuração da instância (injetado pelo indicador).
            public DashboardEstado estado;
            // Callback disparado quando o usuário troca de modo/estratégia pelo painel
            // flutuante — o indicador o registra para reprocessar os sinais na hora.
            public System.Action OnReprocessar = null;

            // Dimensões oficiais do painel — únicas em todo o indicador (usadas também no hit-test de arraste),
            // evitando o antigo bug de números mágicos duplicados que podiam ficar dessincronizados.
            public const float PanelWidth = 1000f;
            public const string VERSAO = "v31";
            // Retângulo do botão FULL/BASIC em coordenadas absolutas (para detecção de clique).
            public SharpDX.RectangleF botaoFullRect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoBasicRect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoSinaisRect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoModoRect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoFecharDashRect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoMinimizarRect = new SharpDX.RectangleF(0, 0, 0, 0);
            // dados da estatística (preenchidos pelo indicador antes do render)
            public int StatGains = 0, StatStops = 0;
            public double StatPontos = 10.0;
            public double StatStopTicks = 20.0;
            public double StatPontosGanhos = 0, StatPontosPerdidos = 0;
            public int StatCompras = 0, StatVendas = 0;
            public System.Collections.Generic.List<string> StatLista = new System.Collections.Generic.List<string>();
            public SharpDX.RectangleF botaoSinal20Rect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoSinal30Rect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoSinal10Rect = new SharpDX.RectangleF(0, 0, 0, 0);
            public SharpDX.RectangleF botaoSinal40Rect = new SharpDX.RectangleF(0, 0, 0, 0);

            private float MedirLarguraTexto(string txt, SharpDX.DirectWrite.TextFormat fmt)
            {
                try
                {
                    using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, txt, fmt, 400, 20))
                        return layout.Metrics.WidthIncludingTrailingWhitespace;
                }
                catch { return txt.Length * 7f; }
            }

            // Desenha um arco parcial (anel do AI Core) começando em startDeg, varrendo
            // sweepDeg graus, na cor dada com opacidade própria. Usado para os anéis
            // rotativos do núcleo de IA.
            private void DesenharAnelAI(RenderTarget rt, float cx, float cy, float raio, float startDeg, float sweepDeg, SharpDX.Direct2D1.SolidColorBrush cor, float stroke, float alpha)
            {
                try
                {
                    float radS = startDeg * (float)Math.PI / 180f;
                    float radE = (startDeg + sweepDeg) * (float)Math.PI / 180f;
                    var p1 = new SharpDX.Vector2(cx + (float)Math.Cos(radS) * raio, cy + (float)Math.Sin(radS) * raio);
                    var p2 = new SharpDX.Vector2(cx + (float)Math.Cos(radE) * raio, cy + (float)Math.Sin(radE) * raio);
                    float o = cor.Opacity;
                    cor.Opacity = alpha;
                    using (var arcGeo = new SharpDX.Direct2D1.PathGeometry(rt.Factory))
                    {
                        using (var sink = arcGeo.Open())
                        {
                            sink.BeginFigure(p1, SharpDX.Direct2D1.FigureBegin.Hollow);
                            sink.AddArc(new SharpDX.Direct2D1.ArcSegment { Point = p2, Size = new SharpDX.Size2F(raio, raio), RotationAngle = 0, SweepDirection = SharpDX.Direct2D1.SweepDirection.Clockwise, ArcSize = sweepDeg > 180 ? SharpDX.Direct2D1.ArcSize.Large : SharpDX.Direct2D1.ArcSize.Small });
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
                            sink.Close();
                        }
                        rt.DrawGeometry(arcGeo, cor, stroke);
                    }
                    cor.Opacity = o;
                }
                catch { }
            }
            public const float PanelHeight = 900f;

            private Indicator indicator;
            private DashboardTheme theme;
            private DashboardResources resources;
            public DashboardMetrics Metrics { get; set; }
            private DashboardAnimations anim;
            private RenderTarget lastRenderTarget; // rastreia o último RT usado, para recriar recursos ao trocar de superfície (gráfico <-> painel flutuante)

            // ── AI Core: estado para transições suaves de cor e expansão ──
            private float _aiR = 1f, _aiG = 1f, _aiB = 1f;   // cor interpolada atual (RGB 0..1)
            private bool  _aiCorInit = false;
            private float _aiEscala = 1f;                    // escala do core (para expansão ao atingir 85%)
            private int   _aiFaixaAnterior = -1;             // faixa anterior (para detectar cruzamento p/ 85%)

            public DashboardEngine(Indicator ind, DashboardEstado est)
            {
                this.indicator = ind;
                this.estado = est;
                theme = new DashboardTheme();
                resources = new DashboardResources();
                Metrics = new DashboardMetrics();
                anim = new DashboardAnimations();
            }

            public void OnRenderTargetChanged(RenderTarget renderTarget)
            {
                if (renderTarget != null)
                {
                    resources.CreateResources(renderTarget, theme);
                    lastRenderTarget = renderTarget;
                }
            }

            // Painel BASIC — minimalista premium: gauge fino + % com legenda + badge sólido.
            private void RenderBasic(RenderTarget renderTarget, DashboardDraw draw, float startX, float startY, float mouseX, float mouseY)
            {
                float panelW = 360f;
                float panelH = 290f;
                float pad = 26f;               // mais respiro nas bordas

                var mainRect = new SharpDX.RectangleF(startX, startY, panelW, panelH);
                var bg = new SharpDX.Direct2D1.RoundedRectangle { Rect = mainRect, RadiusX = 16f, RadiusY = 16f };

                var glow = Metrics.Bias == "COMPRA" ? resources.BrushGlowGreen : (Metrics.Bias == "VENDA" ? resources.BrushGlowRed : null);
                if (glow != null)
                    for (float i = 3f; i >= 1f; i -= 1f)
                    {
                        float sp = i * 2f;
                        renderTarget.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = new SharpDX.RectangleF(mainRect.Left - sp, mainRect.Top - sp, mainRect.Width + sp * 2, mainRect.Height + sp * 2), RadiusX = 16f + sp, RadiusY = 16f + sp }, glow, 1.0f);
                    }

                renderTarget.FillRoundedRectangle(bg, resources.BrushBgMain);
                renderTarget.DrawRoundedRectangle(bg, resources.BrushBorder, 1.2f);

                // ---- HEADER: título à esquerda, pílula toggle à direita, na mesma linha ----
                float headerCY = startY + pad + 9f;   // centro vertical da linha do header

                draw.DrawText("PROFIT", resources.FontValue, new SharpDX.RectangleF(startX + pad, headerCY - 11f, 90, 22), resources.BrushWhite, SharpDX.DirectWrite.TextAlignment.Leading);
                draw.DrawText("PRO", resources.FontLabel, new SharpDX.RectangleF(startX + pad + 62f, headerCY - 8f, 40, 18), resources.BrushGoldFosco, SharpDX.DirectWrite.TextAlignment.Leading);

                // Pílula do toggle (fundo escuro englobando FULL | BASIC)
                {
                    float segW = 48f, pillH = 24f, ph = 3f;
                    float pillW = segW * 2 + ph * 2;
                    float px = startX + panelW - pad - pillW;
                    float py = headerCY - pillH / 2f;

                    var pill = new SharpDX.RectangleF(px, py, pillW, pillH);
                    renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = pill, RadiusX = 12f, RadiusY = 12f }, resources.BrushPillBg);

                    // Segmento ativo com fundo levemente destacado
                    var rFull = new SharpDX.RectangleF(px + ph, py + ph, segW, pillH - ph * 2);
                    var rBasic = new SharpDX.RectangleF(px + ph + segW, py + ph, segW, pillH - ph * 2);

                    if (estado.ModoFull)
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = rFull, RadiusX = 10f, RadiusY = 10f }, resources.BrushCardBg);
                    else
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = rBasic, RadiusX = 10f, RadiusY = 10f }, resources.BrushGoldBadge);

                    draw.DrawText("FULL", resources.FontLabel, new SharpDX.RectangleF(rFull.Left, rFull.Top + 1f, rFull.Width, 16f), estado.ModoFull ? resources.BrushTextPrimary : resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                    draw.DrawText("BASIC", resources.FontLabel, new SharpDX.RectangleF(rBasic.Left, rBasic.Top + 1f, rBasic.Width, 16f), !estado.ModoFull ? resources.BrushGoldFosco : resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);

                    botaoFullRect = rFull;
                    botaoBasicRect = rBasic;
                }

                // ═══════════════════════════════════════════════════════════════
                // AI CORE — núcleo de IA institucional (Tesla / Palantir / Bloomberg)
                // Refinamentos: transição suave de cor (interpolada), data nodes
                // conectados, expansão ~5% ao atingir 85%, pulso a cada 2s no sinal,
                // partículas com velocidades variadas, ripple a cada 5s.
                // ═══════════════════════════════════════════════════════════════
                float cx = startX + panelW / 2f;
                float cy = startY + 148f;
                float raio = 62f;

                double pctv = Math.Max(0, Math.Min(100, Metrics.ConfidenceCurrent));
                float tAnim = Environment.TickCount / 1000f;

                // ── cor-alvo por faixa (RGB 0..1) ──
                // 0-39 branco · 40-54 azul #3B82F6 · 55-84 dourado #D4AF37 · 85+ verde/vermelho
                int faixa; float tr, tg, tb;
                if (pctv >= 85)
                {
                    if (Metrics.Bias == "VENDA") { tr = 0.937f; tg = 0.267f; tb = 0.267f; faixa = 4; } // #EF4444
                    else { tr = 0.133f; tg = 0.773f; tb = 0.369f; faixa = 3; }                          // #22C55E
                }
                else if (pctv >= 55) { tr = 0.831f; tg = 0.686f; tb = 0.216f; faixa = 2; }              // #D4AF37
                else if (pctv >= 40) { tr = 0.231f; tg = 0.510f; tb = 0.965f; faixa = 1; }              // #3B82F6
                else { tr = 0.90f; tg = 0.92f; tb = 0.95f; faixa = 0; }                                  // branco suave

                // ── transição suave (interpolação ~exponencial por frame) ──
                if (!_aiCorInit) { _aiR = tr; _aiG = tg; _aiB = tb; _aiCorInit = true; }
                float lerp = 0.12f;   // ~300-500ms na taxa de refresh típica
                _aiR += (tr - _aiR) * lerp;
                _aiG += (tg - _aiG) * lerp;
                _aiB += (tb - _aiB) * lerp;

                // brush dinâmico com a cor interpolada
                var corCore = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color4(_aiR, _aiG, _aiB, 1f));

                float intensidade = (float)(pctv / 100.0);
                bool sinalPronto = pctv >= 85;

                // heartbeat; no sinal pronto, pulso mais rápido + batida a cada 2s
                float baseFreq = 1.0f + intensidade * 1.6f;
                float pulso = (float)(0.5 + 0.5 * Math.Sin(tAnim * baseFreq));
                float batida2s = 0f;
                if (sinalPronto)
                {
                    float ph = (tAnim % 2f) / 2f;          // 0..1 a cada 2s
                    if (ph < 0.25f) batida2s = (float)Math.Sin(ph / 0.25f * Math.PI) * 0.5f;
                }

                // ── expansão ~5% ao atingir 85% (suave) ──
                float escalaAlvo = sinalPronto ? 1.05f : 1.0f;
                _aiEscala += (escalaAlvo - _aiEscala) * 0.1f;
                float raioE = raio * _aiEscala;
                _aiFaixaAnterior = faixa;

                try
                {
                    // ── HALO / glow ambiente (proporcional à confiança; some abaixo de 55%) ──
                    float glowBase = pctv >= 55 ? 0.06f : 0.03f;
                    float glowAlpha = (glowBase + 0.12f * intensidade) * (1f + batida2s);
                    for (int g = 3; g >= 1; g--)
                    {
                        float gr = raioE + 10f + g * 8f + pulso * 4f;
                        corCore.Opacity = glowAlpha / g;
                        renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), gr, gr), corCore, 2f);
                    }

                    // ── DATA NODES: caminho circular fino + nós que piscam ──
                    corCore.Opacity = 0.12f + 0.10f * intensidade;
                    renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), raioE + 14f, raioE + 14f), corCore, 0.8f);
                    int nNodes = 6;
                    for (int n = 0; n < nNodes; n++)
                    {
                        float na = (360f / nNodes * n + tAnim * 8f) * (float)Math.PI / 180f;
                        float nx = cx + (float)Math.Cos(na) * (raioE + 14f);
                        float ny = cy + (float)Math.Sin(na) * (raioE + 14f);
                        float nblink = (float)(0.3 + 0.7 * Math.Abs(Math.Sin(tAnim * 1.5 + n * 1.3)));
                        corCore.Opacity = (0.3f + 0.4f * intensidade) * nblink;
                        renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(nx, ny), 2.2f, 2.2f), corCore);
                    }

                    // ── OUTER RING (anti-horário) — brilho aumenta no sinal ──
                    float angOuter = -tAnim * 25f;
                    float outerAlpha = 0.5f + (sinalPronto ? 0.3f + batida2s : 0f);
                    DesenharAnelAI(renderTarget, cx, cy, raioE + 6f, angOuter, 220f, corCore, 1.6f, outerAlpha);

                    // ── INNER RING (horário) ──
                    float angInner = tAnim * 40f;
                    DesenharAnelAI(renderTarget, cx, cy, raioE - 4f, angInner, 260f, corCore, 1.4f, 0.78f);

                    // ── MICRO PARTÍCULAS orbitando (velocidades variadas) ──
                    int nPart = 8;
                    float velExtra = sinalPronto ? 10f : 0f;    // aceleram no sinal
                    for (int p = 0; p < nPart; p++)
                    {
                        float baseAng = (360f / nPart) * p + (p * 13 % 40);   // espaçamento "aleatório"
                        float vel = 16f + (p % 3) * 12f + velExtra;
                        float ang = (baseAng + tAnim * vel) * (float)Math.PI / 180f;
                        float orb = raioE + 6f + (p % 2 == 0 ? 6f : -2f);
                        float px = cx + (float)Math.Cos(ang) * orb;
                        float py = cy + (float)Math.Sin(ang) * orb;
                        float blink = (float)(0.4 + 0.6 * Math.Sin(tAnim * 2.0 + p));
                        corCore.Opacity = (0.35f + 0.5f * intensidade) * blink;
                        float pr = 1.6f + intensidade * 1.4f + (sinalPronto ? batida2s * 1.5f : 0f);
                        renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(px, py), pr, pr), corCore);
                    }

                    // ── NÚCLEO central pulsante ──
                    float raioNucleo = (30f + pulso * (3f + intensidade * 4f)) * _aiEscala;
                    corCore.Opacity = 0.10f + 0.16f * intensidade + batida2s * 0.1f;
                    renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), raioNucleo, raioNucleo), corCore);
                    corCore.Opacity = 0.6f + 0.3f * intensidade;
                    renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), raioNucleo, raioNucleo), corCore, 1f);

                    // ── RIPPLE a cada 5s ──
                    float ripplePhase = (tAnim % 5f) / 5f;
                    if (ripplePhase < 0.6f)
                    {
                        float rr2 = raioE * (0.4f + ripplePhase * 1.3f);
                        corCore.Opacity = 0.25f * (1f - ripplePhase / 0.6f);
                        renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), rr2, rr2), corCore, 1.2f);
                    }

                    // ── Texto central: só a % (labels removidos) ──
                    corCore.Opacity = 1f;
                    draw.DrawText(pctv.ToString("F0") + "%", resources.FontBasicPct, new SharpDX.RectangleF(cx - raio, cy - 24f, raio * 2f, 42f), corCore, SharpDX.DirectWrite.TextAlignment.Center);
                }
                catch { }
                finally { corCore.Dispose(); }

                // ---- BADGE sólido com SETA + COMPRA/VENDA ----
                float badgeW = 190f, badgeH = 46f;
                float bx2 = cx - badgeW / 2f;
                float by2 = cy + raio + 20f;
                var badgeRect = new SharpDX.RectangleF(bx2, by2, badgeW, badgeH);
                var rrb = new SharpDX.Direct2D1.RoundedRectangle { Rect = badgeRect, RadiusX = 12f, RadiusY = 12f };

                if (Metrics.CardTipo != 0)
                {
                    string txt = Metrics.CardTexto;
                    var corBadge = Metrics.CardTipo == 1 ? resources.BrushGreenSolido   // COMPRA
                                 : Metrics.CardTipo == 2 ? resources.BrushRedSolido     // VENDA
                                 : Metrics.CardTipo == 3 ? resources.BrushGoldFosco     // PARCIAL
                                 : Metrics.CardTipo == 5 ? resources.BrushGreenSolido   // POSSÍVEL COMPRA
                                 : Metrics.CardTipo == 6 ? resources.BrushRedSolido     // POSSÍVEL VENDA
                                 : resources.BrushRedSolido;                            // CUIDADO
                    renderTarget.FillRoundedRectangle(rrb, corBadge);
                    var fBadge = txt.Length > 7 ? resources.FontValue : resources.FontBasicSinal;
                    draw.DrawText(txt, fBadge, new SharpDX.RectangleF(bx2, by2 + (txt.Length > 7 ? badgeH/2f - 12f : 0), badgeW, txt.Length > 7 ? 24f : badgeH), resources.BrushWhite, SharpDX.DirectWrite.TextAlignment.Center);
                }
                else
                {
                    renderTarget.FillRoundedRectangle(rrb, resources.BrushCardBg);
                    renderTarget.DrawRoundedRectangle(rrb, resources.BrushBorder, 1f);
                    draw.DrawText("AGUARDANDO", resources.FontValue, new SharpDX.RectangleF(bx2, by2 + badgeH / 2f - 12f, badgeW, 24f), resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                }
            }

            // retângulos de config (clique)
            public SharpDX.RectangleF cfgGear30 = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgGear10 = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgGear20 = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgGear40 = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgFechar = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgSalvar = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgOpt1Mais = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgOpt1Menos = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgOpt2Mais = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgOpt2Menos = new SharpDX.RectangleF(0,0,0,0);
            public SharpDX.RectangleF cfgOpt3Toggle = new SharpDX.RectangleF(0,0,0,0);
            // sistema genérico: até 8 controles por modal (menos, mais, toggle) + identificador
            public SharpDX.RectangleF[] cfgLinhaMenos = new SharpDX.RectangleF[8];
            public SharpDX.RectangleF[] cfgLinhaMais = new SharpDX.RectangleF[8];
            public SharpDX.RectangleF[] cfgLinhaToggle = new SharpDX.RectangleF[8];
            public string[] cfgLinhaId = new string[8];
            public int cfgLinhaCount = 0;

            public void Render(RenderTarget renderTarget, float startX, float startY, float mouseX, float mouseY)
            {
                if (resources.BrushBgMain == null || !object.ReferenceEquals(lastRenderTarget, renderTarget))
                {
                    resources.CreateResources(renderTarget, theme);
                    lastRenderTarget = renderTarget;
                }
                if (resources.BrushBgMain == null) return;

                System.Diagnostics.Stopwatch renderClock = System.Diagnostics.Stopwatch.StartNew();
                Metrics.ConfidenceCurrent = anim.Lerp(Metrics.ConfidenceCurrent, Metrics.ConfidenceTarget, 0.08);
                DashboardDraw draw = new DashboardDraw(renderTarget, resources);

                if (!estado.ModoFull)
                {
                    RenderBasic(renderTarget, draw, startX, startY, mouseX, mouseY);
                    return;
                }

                float W = PanelWidth, H = PanelHeight;
                renderTarget.FillRectangle(new SharpDX.RectangleF(startX, startY, W, H), resources.BrushBgMain);

                float M = 20f, G = 16f, cp = 20f;
                float ix = startX + M, iw = W - M*2;

                string estadoSinal = Metrics.EstadoSinal ?? "AGUARDANDO";
                bool isVenda = estadoSinal.Contains("VENDA");
                bool isCompra = estadoSinal.Contains("COMPRA");
                bool ativo = isVenda || isCompra;
                double forca = Metrics.ConfidenceTarget;
                bool possivel = !ativo && forca >= 75.0;
                bool dirVenda = ativo ? isVenda : (Metrics.Bias == "VENDA");
                SharpDX.Direct2D1.SolidColorBrush accent = !ativo ? (possivel ? resources.BrushGold : resources.BrushTextSecondary) : (isVenda ? resources.BrushRed : resources.BrushGreen);
                // cor dourada do gauge a partir de 55%
                SharpDX.Direct2D1.SolidColorBrush gaugeCor = forca >= 55.0 ? resources.BrushGold : resources.BrushTextPrimary;

                // BORDA DO PAINEL INTEIRO — verde (compra) / vermelho (venda) quando há sinal
                if (ativo)
                {
                    var pRect = new SharpDX.RectangleF(startX + 1, startY + 1, W - 2, H - 2);
                    var pRR = new SharpDX.Direct2D1.RoundedRectangle { Rect = pRect, RadiusX = 4f, RadiusY = 4f };
                    // glow suave interno
                    float so = accent.Opacity;
                    accent.Opacity = 0.15f;
                    renderTarget.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = new SharpDX.RectangleF(startX+3, startY+3, W-6, H-6), RadiusX = 4f, RadiusY = 4f }, accent, 4f);
                    accent.Opacity = so;
                    // borda principal
                    renderTarget.DrawRoundedRectangle(pRR, accent, 2f);
                }

                // ══════════════════════════════════════════════════
                // HEADER — duas faixas alinhadas
                //   Faixa 1: [logo + título]  ·  [monitorando]  ·  [relógio]
                //   Faixa 2: [botões 3.0/2.0/1.0 + FULL/BASIC] à direita
                // ══════════════════════════════════════════════════
                float row1CY = startY + 30f;   // centro vertical da faixa 1

                // LOGO — cabeça de cervo (deer) dourada, centralizada em row1CY
                {
                    float lcx = ix + 22, lcy = row1CY + 3;
                    var g = resources.BrushGold;
                    renderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(lcx, lcy + 2), 9f, 12f), g, 1.6f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx-7, lcy-4), new SharpDX.Vector2(lcx-13, lcy-8), g, 1.6f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx+7, lcy-4), new SharpDX.Vector2(lcx+13, lcy-8), g, 1.6f);
                    // galhadas esquerda
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx-5, lcy-10), new SharpDX.Vector2(lcx-9, lcy-20), g, 1.8f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx-7, lcy-14), new SharpDX.Vector2(lcx-13, lcy-16), g, 1.5f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx-9, lcy-20), new SharpDX.Vector2(lcx-14, lcy-22), g, 1.5f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx-9, lcy-20), new SharpDX.Vector2(lcx-6, lcy-24), g, 1.5f);
                    // galhadas direita
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx+5, lcy-10), new SharpDX.Vector2(lcx+9, lcy-20), g, 1.8f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx+7, lcy-14), new SharpDX.Vector2(lcx+13, lcy-16), g, 1.5f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx+9, lcy-20), new SharpDX.Vector2(lcx+14, lcy-22), g, 1.5f);
                    renderTarget.DrawLine(new SharpDX.Vector2(lcx+9, lcy-20), new SharpDX.Vector2(lcx+6, lcy-24), g, 1.5f);
                    renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(lcx, lcy+11), 2f, 1.6f), g);
                }
                // TÍTULO — baseline em row1CY
                draw.DrawText("PROFIT ACADEMY", resources.FontTitle, new SharpDX.RectangleF(ix + 46, row1CY - 11, 220, 22), resources.BrushTextPrimary);
                draw.DrawText("PRO", resources.FontTitle, new SharpDX.RectangleF(ix + 220, row1CY - 11, 44, 22), resources.BrushGold);
                draw.DrawText("v1", resources.FontSmall, new SharpDX.RectangleF(ix + 260, row1CY - 2, 24, 14), resources.BrushTextSecondary);

                // MONITORANDO {sym} — abaixo do título, alinhado com "PROFIT ACADEMY"
                string sym = "MNQ";
                if (indicator != null && indicator.Instrument != null && indicator.Instrument.MasterInstrument != null)
                    sym = indicator.Instrument.MasterInstrument.Name ?? "MNQ";
                {
                    string monTxt = "MONITORANDO " + sym;
                    float monX = ix + 46f;              // alinhado com o texto do título
                    float monY = row1CY + 26f;          // um pouco mais abaixo
                    renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(monX + 3, monY), 4.5f, 4.5f), resources.BrushGreen);
                    draw.DrawText(monTxt, resources.FontMonitor, new SharpDX.RectangleF(monX + 14, monY - 9, 260, 18), resources.BrushTextSecondary);
                }

                // FAIXA 2 — botões alinhados à direita; relógio no CENTRO do cabeçalho
                float clkW = 118f, clkH = 30f;
                {
                    float by = row1CY - 13, bh = 30f, bw = 56f, bg = 7f;   // subidos p/ a linha do header, maiores
                    float fbW = 50f;
                    float bloco1 = bw*2 + 7;   // dois botões (1.0 e 2.0)
                    float sigW = fbW + 14;     // botão SINAIS um pouco maior
                    float blocoBotoes = bloco1 + 14 + fbW + 5 + fbW + 5 + sigW; // 1.0 2.0 FULL BASIC SINAIS
                    // agora os botões vão até a borda direita; o relógio sai daqui p/ o centro
                    float bx = ix + iw - blocoBotoes;
                    System.Action<SharpDX.RectangleF,string,bool,SharpDX.Direct2D1.SolidColorBrush> pillBig = (r,label,on,col) => {
                        var rr = new SharpDX.Direct2D1.RoundedRectangle { Rect = r, RadiusX = 6f, RadiusY = 6f };
                        if (on) { float o = col.Opacity; col.Opacity = 0.16f; renderTarget.FillRoundedRectangle(rr, col); col.Opacity = o; }
                        renderTarget.DrawRoundedRectangle(rr, on ? col : resources.BrushBorder, 1.4f);
                        draw.DrawText(label, resources.FontValue, new SharpDX.RectangleF(r.Left, r.Top + 7f, r.Width, 16f), on ? col : resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                    };
                    // Sinal 1.0 (regiões) e Sinal 2.0 (tendência ancorada em EMA). Um ativo por vez.
                    bool on10 = !estado.Sinal20;
                    bool on20 = estado.Sinal20;
                    var r10 = new SharpDX.RectangleF(bx, by, bw, bh); pillBig(r10,"1.0",on10,resources.BrushGold); botaoSinal10Rect = r10;
                    var r20 = new SharpDX.RectangleF(bx+bw+7, by, bw, bh); pillBig(r20,"2.0",on20,resources.BrushBlue); botaoSinal20Rect = r20;
                    // engrenagens removidas do dashboard: desativa os rects (sem cliques fantasma)
                    cfgGear10 = cfgGear20 = new SharpDX.RectangleF(0,0,0,0);
                    // desativa os rects dos botões removidos (evita cliques fantasma)
                    botaoSinal40Rect = botaoSinal30Rect = new SharpDX.RectangleF(0,0,0,0);
                    cfgGear40 = cfgGear30 = new SharpDX.RectangleF(0,0,0,0);
                    // FULL / BASIC
                    float fbX = bx + bloco1 + 14;
                    var rF = new SharpDX.RectangleF(fbX, by, fbW, bh); pillBig(rF,"FULL",estado.ModoFull,resources.BrushGreen); botaoFullRect = rF;
                    var rB = new SharpDX.RectangleF(fbX+(fbW+5), by, fbW, bh); pillBig(rB,"BASIC",!estado.ModoFull,resources.BrushTextSecondary); botaoBasicRect = rB;
                    // botão SINAIS (abre painel de estatísticas) — abaixo e centralizado
                    // entre o FULL e o BASIC.
                    float larguraFB = (fbW + 5) + fbW;                 // extensão total FULL+BASIC
                    float sigYY = by + bh + 4;                         // logo abaixo dos dois
                    float sigXX = fbX + (larguraFB - sigW) / 2f;       // centralizado no meio
                    var rSig = new SharpDX.RectangleF(sigXX, sigYY, sigW, bh);
                    pillBig(rSig, "SINAIS", estado.StatAberto, resources.BrushBlue);
                    botaoSinaisRect = rSig;
                }

                // RELÓGIO — centralizado no MEIO do cabeçalho (entre o logo e os botões)
                float clockCX = ix + iw * 0.44f;   // ~centro do header
                var clockR = new SharpDX.RectangleF(clockCX - clkW/2f, row1CY - clkH/2f, clkW, clkH);
                renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = clockR, RadiusX = 8f, RadiusY = 8f }, resources.BrushCardBg);
                renderTarget.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = clockR, RadiusX = 8f, RadiusY = 8f }, resources.BrushBorder, 1f);
                draw.DrawText(DateTime.Now.ToString("HH:mm:ss"), resources.FontValue, new SharpDX.RectangleF(clockR.Left, clockR.Top + 7, clockR.Width, 18), resources.BrushTextPrimary, SharpDX.DirectWrite.TextAlignment.Center);

                // BOTÃO MODO — Conservador (verde, ✕ cancelado ligado) / Agressivo (laranja, sem cancelamento).
                // Fica à DIREITA do relógio.
                {
                    float mW = 118f, mH = clkH;
                    var modoR = new SharpDX.RectangleF(clockR.Right + 10f, row1CY - mH/2f, mW, mH);
                    bool cons = estado.ModoConservador;
                    var corModo = cons ? resources.BrushGreen : resources.BrushGold;
                    var rrM = new SharpDX.Direct2D1.RoundedRectangle { Rect = modoR, RadiusX = 8f, RadiusY = 8f };
                    float oM = corModo.Opacity; corModo.Opacity = 0.16f; renderTarget.FillRoundedRectangle(rrM, corModo); corModo.Opacity = oM;
                    renderTarget.DrawRoundedRectangle(rrM, corModo, 1.4f);
                    draw.DrawText(cons ? "CONSERVADOR" : "AGRESSIVO", resources.FontValue, new SharpDX.RectangleF(modoR.Left, modoR.Top + 7, modoR.Width, 18), corModo, SharpDX.DirectWrite.TextAlignment.Center);
                    botaoModoRect = modoR;
                }

                // linha divisória sutil separando o header do conteúdo
                using (var hdv = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(28,33,41,180)))
                    renderTarget.DrawLine(new SharpDX.Vector2(ix, startY + 104f), new SharpDX.Vector2(ix + iw, startY + 104f), hdv, 1f);

                // ── BOTÕES X (fechar) e − (minimizar) no CANTO SUPERIOR DIREITO ──
                {
                    float bs = 22f;                       // tamanho dos botões
                    float bxr = ix + iw - bs - 6f;        // X no canto direito
                    float bmr = bxr - bs - 6f;            // − à esquerda do X
                    float byr = startY + 8f;
                    var rX = new SharpDX.RectangleF(bxr, byr, bs, bs);
                    var rMin = new SharpDX.RectangleF(bmr, byr, bs, bs);
                    // fundo suave
                    using (var bg = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(40,46,56,200)))
                    {
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = rX, RadiusX = 5f, RadiusY = 5f }, bg);
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = rMin, RadiusX = 5f, RadiusY = 5f }, bg);
                    }
                    draw.DrawText("\u2715", resources.FontLabel, new SharpDX.RectangleF(rX.Left, rX.Top + 3f, rX.Width, 16f), resources.BrushRed, SharpDX.DirectWrite.TextAlignment.Center);
                    draw.DrawText("\u2013", resources.FontValue, new SharpDX.RectangleF(rMin.Left, rMin.Top + 2f, rMin.Width, 16f), resources.BrushTextPrimary, SharpDX.DirectWrite.TextAlignment.Center);
                    botaoFecharDashRect = rX;
                    botaoMinimizarRect = rMin;
                }

                float contentY = startY + 114f;
                float contentH = H - (contentY - startY) - 56f - M; // -56 rodapé

                // ══════════════════════════════════════════════════
                // LINHA 1: GAUGE (esq) + SINAL matrix (dir)
                // ══════════════════════════════════════════════════
                float row1H = contentH * 0.42f;
                float halfW = (iw - G) / 2f;

                // -- CARD GAUGE --
                SharpDX.RectangleF pGauge = new SharpDX.RectangleF(ix, contentY, halfW, row1H);
                draw.DrawGlassCard(pGauge, mouseX, mouseY);
                float gSz = Math.Min(halfW - cp*2, row1H - cp*2);
                float gX = pGauge.Left + (halfW - gSz)/2f;
                float gY = pGauge.Top + (row1H - gSz)/2f;
                string confLbl = forca >= 85 ? "ALTA CONFIAN\u00C7A" : (forca >= 60 ? "CONFIAN\u00C7A M\u00C9DIA" : "BAIXA CONFIAN\u00C7A");
                draw.BiasVendaAI = Metrics.Bias == "VENDA";
                draw.DrawRingProgress(gX, gY, gSz, gSz, Metrics.ConfidenceCurrent, confLbl, gaugeCor, mouseX, mouseY);

                // -- CARD SINAL (matrix) --
                // Cor do card SINAL: verde(compra)/vermelho(venda) conforme direção.
                // Sem sinal → fundo neutro claro e texto "AGUARDANDO".
                bool sigVenda = ativo ? isVenda : dirVenda;
                bool sigTemDir = ativo || possivel || Metrics.Bias == "VENDA" || Metrics.Bias == "COMPRA";
                SharpDX.Direct2D1.SolidColorBrush sigAccent = !sigTemDir ? resources.BrushTextSecondary : (sigVenda ? resources.BrushRed : resources.BrushGreen);

                SharpDX.RectangleF pSig = new SharpDX.RectangleF(ix + halfW + G, contentY, halfW, row1H);
                draw.DrawGlassCard(pSig, mouseX, mouseY);
                var pSigRR = new SharpDX.Direct2D1.RoundedRectangle { Rect = pSig, RadiusX = 12f, RadiusY = 12f };

                // Cor e intensidade do fundo conforme o ESTADO DO CARD.
                // COMPRA (1) → verde forte · VENDA (2) → vermelho forte ·
                // POSSÍVEL COMPRA (5)/VENDA (6) → tom suave · PARCIAL (3)/CUIDADO (4) → dourado/vermelho leve.
                int ct = Metrics.CardTipo;
                SharpDX.Direct2D1.SolidColorBrush cardCor;
                float cardTint;
                if (ct == 1)      { cardCor = resources.BrushGreen; cardTint = 0.32f; }   // COMPRA
                else if (ct == 2) { cardCor = resources.BrushRed;   cardTint = 0.32f; }   // VENDA
                else if (ct == 5) { cardCor = resources.BrushGreen; cardTint = 0.14f; }   // POSSÍVEL COMPRA
                else if (ct == 6) { cardCor = resources.BrushRed;   cardTint = 0.14f; }   // POSSÍVEL VENDA
                else if (ct == 3) { cardCor = resources.BrushGold;  cardTint = 0.16f; }   // PARCIAL
                else if (ct == 4) { cardCor = resources.BrushRed;   cardTint = 0.18f; }   // CUIDADO
                else              { cardCor = null;                 cardTint = 0f; }      // neutro

                if (cardCor != null)
                {
                    float o = cardCor.Opacity; cardCor.Opacity = cardTint;
                    renderTarget.FillRoundedRectangle(pSigRR, cardCor);
                    cardCor.Opacity = o;
                }
                else
                {
                    // neutro: leve tom claro (branco esbatido)
                    using (var wb = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(255, 255, 255, 14)))
                        renderTarget.FillRoundedRectangle(pSigRR, wb);
                }
                // borda do card na cor do estado (branca quando neutro)
                {
                    var borda = cardCor ?? resources.BrushTextSecondary;
                    float o = borda.Opacity; borda.Opacity = cardCor != null ? 0.8f : 0.4f;
                    renderTarget.DrawRoundedRectangle(pSigRR, borda, 1.8f);
                    borda.Opacity = o;
                }
                // acento vertical esquerdo
                renderTarget.FillRectangle(new SharpDX.RectangleF(pSig.Left + 3, pSig.Top + 14, 3f, row1H - 28), cardCor ?? resources.BrushTextSecondary);
                // efeito "matrix rain" sutil (otimizado: menos colunas, sem Random por frame)
                if (cardCor != null)
                {
                    float t = Environment.TickCount / 1000f;
                    int cols = 10;   // reduzido de 22 (bem menos linhas por frame)
                    float o0 = cardCor.Opacity;
                    for (int c = 0; c < cols; c++)
                    {
                        float px = pSig.Left + 30 + c * ((halfW - 50) / (float)cols);
                        float off = ((c * 37) % 50) / 50f;   // pseudo-aleatório fixo, sem Random
                        float phase = (float)((t * 0.5 + off * 5) % 1.0);
                        float py = pSig.Top + 20 + phase * (row1H - 40);
                        cardCor.Opacity = 0.12f;
                        renderTarget.DrawLine(new SharpDX.Vector2(px, py), new SharpDX.Vector2(px, py + 8), cardCor, 1f);
                    }
                    cardCor.Opacity = o0;
                }
                float sigCX = pSig.Left + halfW/2f;
                draw.DrawText("SINAL", resources.FontLabel, new SharpDX.RectangleF(pSig.Left, pSig.Top + 24, halfW, 16), resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                // texto grande: usa o estado do card (COMPRA/VENDA/POSSÍVEL PARCIAL/CUIDADO),
                // caindo para AGUARDANDO quando não há sinal ativo.
                string sigTxt;
                SharpDX.Direct2D1.SolidColorBrush sigTxtCor;
                if (Metrics.CardTipo != 0)
                {
                    sigTxt = Metrics.CardTexto;
                    sigTxtCor = Metrics.CardTipo == 1 ? resources.BrushGreen        // COMPRA
                              : Metrics.CardTipo == 2 ? resources.BrushRed          // VENDA
                              : Metrics.CardTipo == 3 ? resources.BrushGoldFosco    // POSSÍVEL PARCIAL
                              : Metrics.CardTipo == 5 ? resources.BrushGreen        // POSSÍVEL COMPRA
                              : Metrics.CardTipo == 6 ? resources.BrushRed          // POSSÍVEL VENDA
                              : resources.BrushRed;                                 // CUIDADO
                }
                else
                {
                    sigTxt = sigTemDir ? (sigVenda ? "VENDA" : "COMPRA") : "AGUARDANDO";
                    sigTxtCor = sigTemDir ? sigAccent : resources.BrushTextPrimary;
                }
                float sigTxtY = pSig.Top + row1H*0.34f;
                // fonte menor para textos longos (POSSÍVEL COMPRA/VENDA/PARCIAL, CUIDADO)
                var sigFonte = sigTxt.Length > 7 ? resources.FontSignalMed : resources.FontSignal;
                draw.DrawText(sigTxt, sigFonte, new SharpDX.RectangleF(pSig.Left, sigTxtY, halfW, 56), sigTxtCor, SharpDX.DirectWrite.TextAlignment.Center);
                // SETA triangular grande — só nos sinais CONFIRMADOS (compra=cima, venda=baixo).
                // Tipos: 1=compra confirmada, 2=venda confirmada. Pré-sinal e outros não têm seta.
                bool mostrarSeta = Metrics.CardTipo == 1 || Metrics.CardTipo == 2;
                bool setaVenda = Metrics.CardTipo == 2;
                if (mostrarSeta)
                {
                    var setaCor = setaVenda ? resources.BrushRed : resources.BrushGreen;
                    float arX = pSig.Left + 40f;
                    float arY = sigTxtY + 24f;
                    float aw = 18f, ah = 20f;
                    using (var tri = new SharpDX.Direct2D1.PathGeometry(renderTarget.Factory))
                    {
                        using (var sk = tri.Open())
                        {
                            if (setaVenda)
                            {
                                // seta PARA BAIXO (venda)
                                sk.BeginFigure(new SharpDX.Vector2(arX, arY - ah/2f), SharpDX.Direct2D1.FigureBegin.Filled);
                                sk.AddLine(new SharpDX.Vector2(arX + aw, arY - ah/2f));
                                sk.AddLine(new SharpDX.Vector2(arX + aw/2f, arY + ah/2f));
                            }
                            else
                            {
                                sk.BeginFigure(new SharpDX.Vector2(arX + aw/2f, arY - ah/2f), SharpDX.Direct2D1.FigureBegin.Filled);
                                sk.AddLine(new SharpDX.Vector2(arX + aw, arY + ah/2f));
                                sk.AddLine(new SharpDX.Vector2(arX, arY + ah/2f));
                            }
                            sk.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                            sk.Close();
                        }
                        renderTarget.FillGeometry(tri, setaCor);
                    }
                }
                // fluxo institucional + badge
                draw.DrawText("Fluxo Institucional", resources.FontLabel, new SharpDX.RectangleF(pSig.Left, pSig.Bottom - 72, halfW, 16), resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                {
                    string fluxo = Metrics.FlowStr ?? "NEUTRO";
                    var bd = new SharpDX.RectangleF(sigCX - 80, pSig.Bottom - 50, 160, 30);
                    var fcol = fluxo == "COMPRADOR" ? resources.BrushGreen : (fluxo == "VENDEDOR" ? resources.BrushRed : resources.BrushTextSecondary);
                    float o = fcol.Opacity; fcol.Opacity = 0.12f;
                    renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = bd, RadiusX = 8f, RadiusY = 8f }, fcol);
                    fcol.Opacity = o;
                    renderTarget.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = bd, RadiusX = 8f, RadiusY = 8f }, fcol, 1f);
                    draw.DrawText(fluxo, resources.FontValue, new SharpDX.RectangleF(bd.Left, bd.Top + 7, bd.Width, 18), fcol, SharpDX.DirectWrite.TextAlignment.Center);
                }

                // ── CAMADA 1: veredito de qualidade de mercado (topo do card SINAL) ──
                if (Metrics.CtxAtivo)
                {
                    float qy = pSig.Top + 44f;
                    // regime + estrelas
                    var estColor = Metrics.CtxEstrelas >= 4 ? resources.BrushGreen
                                 : (Metrics.CtxEstrelas == 3 ? resources.BrushGold : resources.BrushRed);
                    string estrelas = ContextoMercado.Estrelas(Metrics.CtxEstrelas);
                    draw.DrawText(estrelas + "  " + Metrics.CtxRegime, resources.FontLabel,
                        new SharpDX.RectangleF(pSig.Left, qy, halfW, 16), estColor, SharpDX.DirectWrite.TextAlignment.Center);
                    // veredito posso operar
                    if (!Metrics.CtxPodeOperar)
                        draw.DrawText("\u26D4 " + Metrics.CtxMotivoBloqueio, resources.FontSmall,
                            new SharpDX.RectangleF(pSig.Left, qy + 17, halfW, 12), resources.BrushRed, SharpDX.DirectWrite.TextAlignment.Center);
                }

                // ══════════════════════════════════════════════════
                // LINHA 2: 4 CARDS DE MÉTRICAS (2×2)
                // ══════════════════════════════════════════════════
                float row2Y = contentY + row1H + G;
                float row2H = contentH * 0.34f;
                float mCardW = (iw - G) / 2f;
                float mCardH = (row2H - G) / 2f;

                double conf = forca;
                double liq = Math.Max(0, conf - 5);
                double zone = Metrics.ZoneScore > 0 ? Metrics.ZoneScore : conf * 0.9;
                int seg10(double v) => Math.Max(1, Math.Min(10, (int)Math.Round(v/10.0)));

                // "Último movimento": direção favorecida (sinal ativo → sua direção; senão viés/delta)
                bool favVenda;
                if (ativo) favVenda = isVenda;
                else if (Metrics.Bias == "VENDA") favVenda = true;
                else if (Metrics.Bias == "COMPRA") favVenda = false;
                else favVenda = Metrics.CurrentDelta < 0; // fallback: delta
                bool temDirecao = ativo || Metrics.Bias == "VENDA" || Metrics.Bias == "COMPRA";

                // Estrutura e Supply Zone: verde=compra, vermelho=venda (favorece último movimento)
                SharpDX.Direct2D1.SolidColorBrush corDir = favVenda ? resources.BrushRed : resources.BrushGreen;
                SharpDX.Direct2D1.SolidColorBrush cEstrut = temDirecao ? corDir : resources.BrushTextSecondary;
                SharpDX.Direct2D1.SolidColorBrush cPress = temDirecao ? corDir : resources.BrushTextSecondary;
                SharpDX.Direct2D1.SolidColorBrush cLiq = resources.BrushBlue;   // azul claro sempre
                SharpDX.Direct2D1.SolidColorBrush cZone = temDirecao ? corDir : resources.BrushTextSecondary;

                string estrutS = !temDirecao ? "Neutra" : (favVenda ? "Vendedora" : "Compradora");
                string pressS  = !temDirecao ? "Neutra" : (favVenda ? "Vendedora" : "Compradora");
                string liqS = liq >= 60 ? "Adequada" : (liq >= 30 ? "M\u00E9dia" : "Baixa");
                string zoneS   = !temDirecao ? "Neutra" : (favVenda ? "Venda" : "Compra");

                draw.DrawTechIndicator(ix, row2Y, mCardW, mCardH, "Estrutura", estrutS, $"{conf:F0}%", cEstrut, seg10(conf), "Estrutura", mouseX, mouseY);
                draw.DrawTechIndicator(ix + mCardW + G, row2Y, mCardW, mCardH, "Press\u00E3o", pressS, $"{conf:F0}%", cPress, seg10(conf), "Pressao", mouseX, mouseY);
                draw.DrawTechIndicator(ix, row2Y + mCardH + G, mCardW, mCardH, "Liquidez", liqS, $"{liq:F0}%", cLiq, seg10(liq), "Liquidez", mouseX, mouseY);
                draw.DrawTechIndicator(ix + mCardW + G, row2Y + mCardH + G, mCardW, mCardH, "Supply Zone", zoneS, $"{zone:F0}%", cZone, seg10(zone), "Supply", mouseX, mouseY);

                // ══════════════════════════════════════════════════
                // LINHA 3: IA ANALYSIS + gráfico de fluxo
                // ══════════════════════════════════════════════════
                float row3Y = row2Y + row2H + G;
                float row3H = contentH - row1H - row2H - G*2;
                SharpDX.RectangleF pIA = new SharpDX.RectangleF(ix, row3Y, iw, row3H);
                draw.DrawGlassCard(pIA, mouseX, mouseY);
                // acento azul esquerdo
                renderTarget.FillRectangle(new SharpDX.RectangleF(pIA.Left + 3, pIA.Top + 14, 3f, row3H - 28), resources.BrushBlue);
                // ícone neural MAIOR e azul, centralizado verticalmente
                float nx = pIA.Left + 60, ny = pIA.Top + row3H*0.44f;
                float pn = (float)(Environment.TickCount/700.0);
                float ringR = 22f; // maior
                for (int i = 0; i < 7; i++)
                {
                    float a = (float)(i*(Math.PI*2/7) + pn*0.2);
                    var np = new SharpDX.Vector2(nx + (float)Math.Cos(a)*ringR, ny + (float)Math.Sin(a)*ringR);
                    float no = resources.BrushBlue.Opacity;
                    resources.BrushBlue.Opacity = 0.4f;
                    renderTarget.DrawLine(new SharpDX.Vector2(nx, ny), np, resources.BrushBlue, 1.3f);
                    resources.BrushBlue.Opacity = 1f;
                    renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(np, 3f, 3f), resources.BrushBlue);
                    resources.BrushBlue.Opacity = no;
                }
                renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(nx, ny), 5f, 5f), resources.BrushBlue);
                draw.DrawText("IA ANALYSIS", resources.FontLabel, new SharpDX.RectangleF(pIA.Left + 20, ny + 30, 80, 14), resources.BrushBlue, SharpDX.DirectWrite.TextAlignment.Center);
                // bullets — centralizados verticalmente no card
                string avisoBriga = Metrics.IaMensagem ?? "";
                if (!string.IsNullOrEmpty(avisoBriga))
                {
                    float ax = pIA.Left + 108;
                    float ayC = pIA.Top + row3H/2f - 18f;
                    draw.DrawText("\u26A0", resources.FontIA, new SharpDX.RectangleF(ax, ayC, 24, 22), resources.BrushGold);
                    draw.DrawText(avisoBriga, resources.FontIA, new SharpDX.RectangleF(ax + 26, ayC - 4, iw*0.40f, 40), resources.BrushGold);
                }
                else
                {
                    string[] bullets = Metrics.IABullets ?? new string[0];
                    if (bullets.Length == 0) bullets = new[] { "Aguardando confluência.", "Sem sinal ativo." };
                    int nb = Math.Min(4, bullets.Length);
                    float lineH = 24f;
                    float blocoH = nb * lineH;
                    float bStartY = pIA.Top + (row3H - blocoH)/2f;
                    float bx2 = pIA.Left + 108;
                    for (int i = 0; i < nb; i++)
                        draw.DrawText(bullets[i], resources.FontIA, new SharpDX.RectangleF(bx2, bStartY + i*lineH, iw*0.40f, 20), resources.BrushTextPrimary);
                }
                // ── GRÁFICO DE PREÇO estilo TradingView ──
                float chX0 = pIA.Left + iw*0.50f, chXE = pIA.Right - 82f; // reserva espaço p/ label de preço
                float chTop = pIA.Top + 26, chBot = pIA.Bottom - 22;

                double[] hist = Metrics.PrecoHistorico ?? new double[0];
                // título com nome + variação
                string nomeAtivo = "Mini Nasdaq-100 Futures";
                if (indicator != null && indicator.Instrument != null && indicator.Instrument.MasterInstrument != null)
                    nomeAtivo = indicator.Instrument.MasterInstrument.Name ?? nomeAtivo;
                // tendência dos últimos ~2min (compara início vs fim do buffer)
                bool altaTend = Metrics.PrecoVariacao >= 0;
                var precoCol = altaTend ? resources.BrushGreen : resources.BrushRed;

                draw.DrawText(nomeAtivo, resources.FontValue, new SharpDX.RectangleF(chX0, pIA.Top + 6, 200, 16), resources.BrushTextPrimary);
                if (hist.Length >= 2)
                {
                    string sinalVar = Metrics.PrecoVariacao >= 0 ? "+" : "";
                    string sinalPct = Metrics.PrecoVariacaoPct >= 0 ? "+" : "";
                    string varTxt = $"{Metrics.PrecoAtual:F1}   {sinalVar}{Metrics.PrecoVariacao:F1} ({sinalPct}{Metrics.PrecoVariacaoPct:F2}%)";
                    draw.DrawText(varTxt, resources.FontValue, new SharpDX.RectangleF(chX0 + 150, pIA.Top + 6, 280, 16), precoCol);
                }

                if (hist.Length >= 2)
                {
                    // escala vertical (min/max do buffer)
                    double pmin = double.MaxValue, pmax = double.MinValue;
                    foreach (var p in hist) { if (p < pmin) pmin = p; if (p > pmax) pmax = p; }
                    double range = pmax - pmin;
                    if (range <= 0) range = 1;
                    // margem de 8% em cima/embaixo
                    double margem = range * 0.08;
                    pmin -= margem; pmax += margem; range = pmax - pmin;

                    int n = hist.Length;
                    var pts = new SharpDX.Vector2[n];
                    for (int i = 0; i < n; i++)
                    {
                        float t = i/(float)(n-1);
                        float px = chX0 + t*(chXE - chX0);
                        float py = chBot - (float)((hist[i]-pmin)/range) * (chBot - chTop);
                        pts[i] = new SharpDX.Vector2(px, py);
                    }

                    // área preenchida sob a linha (gradiente simulado por opacidade decrescente)
                    float fillOp0 = precoCol.Opacity;
                    for (int i = 0; i < n-1; i++)
                    {
                        // faixa vertical de cada segmento até a base
                        int steps = 6;
                        float colH = chBot - pts[i].Y;
                        if (colH <= 0) continue;
                        float stepH = colH / steps;
                        for (int s = 0; s < steps; s++)
                        {
                            precoCol.Opacity = 0.16f * (1f - s/(float)steps);
                            float segTop = pts[i].Y + s*stepH;
                            renderTarget.DrawLine(new SharpDX.Vector2(pts[i].X, segTop), new SharpDX.Vector2(pts[i].X, segTop + stepH + 0.5f), precoCol, 1.5f);
                        }
                    }
                    precoCol.Opacity = fillOp0;

                    // linha do preço (fina, cor da tendência)
                    for (int i = 0; i < n-1; i++)
                        renderTarget.DrawLine(pts[i], pts[i+1], precoCol, 1.4f);

                    // linha pontilhada horizontal no preço atual + label à direita
                    float lastY = pts[n-1].Y;
                    float o = precoCol.Opacity; precoCol.Opacity = 0.5f;
                    for (float dx = chX0; dx < chXE; dx += 8f)
                        renderTarget.DrawLine(new SharpDX.Vector2(dx, lastY), new SharpDX.Vector2(dx + 4f, lastY), precoCol, 0.8f);
                    precoCol.Opacity = o;
                    // label de preço (caixinha à direita) — maior e mais legível
                    var lblR = new SharpDX.RectangleF(chXE + 4, lastY - 12, 74, 24);
                    renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = lblR, RadiusX = 4f, RadiusY = 4f }, precoCol);
                    draw.DrawText($"{Metrics.PrecoAtual:F1}", resources.FontValue, new SharpDX.RectangleF(lblR.Left, lblR.Top + 4, lblR.Width, 16), resources.BrushBgMain, SharpDX.DirectWrite.TextAlignment.Center);
                    // ponto na ponta
                    renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(pts[n-1], 3.5f, 3.5f), precoCol);
                }
                else
                {
                    draw.DrawText("Coletando dados de pre\u00E7o...", resources.FontSmall, new SharpDX.RectangleF(chX0, (chTop+chBot)/2f - 6, 220, 14), resources.BrushTextSecondary);
                }

                // ══════════════════════════════════════════════════
                // RODAPÉ: PRO · ping · MNQ · NT8 · ONLINE
                // ══════════════════════════════════════════════════
                float footY = startY + H - 44f;
                using (var dv = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(28,33,41,255)))
                    renderTarget.DrawLine(new SharpDX.Vector2(ix, footY - 8), new SharpDX.Vector2(ix + iw, footY - 8), dv, 1f);
                double renderMs = renderClock.Elapsed.TotalMilliseconds;
                float fseg = iw / 5f;
                draw.DrawText("PRO", resources.FontTitle, new SharpDX.RectangleF(ix, footY, fseg, 22), resources.BrushGold, SharpDX.DirectWrite.TextAlignment.Center);
                // PING com ícone de sinal de internet (barras crescentes)
                {
                    string pingTxt = (renderMs < 1 ? "<1 ms" : $"{renderMs:F0} ms");
                    // desenha as 4 barrinhas de sinal + texto, centralizados no segmento
                    float segCX = ix + fseg + fseg/2f;
                    float txtW = pingTxt.Length * 8f;
                    float iconW = 20f;
                    float grpX = segCX - (iconW + 6 + txtW)/2f;
                    float baseY = footY + 15f;
                    for (int b = 0; b < 4; b++)
                    {
                        float bh2 = 4f + b*3f;
                        renderTarget.FillRectangle(new SharpDX.RectangleF(grpX + b*5f, baseY - bh2, 3f, bh2), resources.BrushGreen);
                    }
                    draw.DrawText(pingTxt, resources.FontValue, new SharpDX.RectangleF(grpX + iconW + 6, footY + 2, txtW + 10, 18), resources.BrushGreen);
                }
                draw.DrawText("\u2295 " + sym, resources.FontValue, new SharpDX.RectangleF(ix + fseg*2, footY + 2, fseg, 18), resources.BrushTextPrimary, SharpDX.DirectWrite.TextAlignment.Center);
                draw.DrawText("\u25A4 NT8", resources.FontValue, new SharpDX.RectangleF(ix + fseg*3, footY + 2, fseg, 18), resources.BrushTextPrimary, SharpDX.DirectWrite.TextAlignment.Center);
                renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(ix + fseg*4 + 30, footY + 11), 4f, 4f), resources.BrushGreen);
                draw.DrawText("ONLINE", resources.FontValue, new SharpDX.RectangleF(ix + fseg*4 + 40, footY + 2, fseg - 40, 18), resources.BrushGreen);

                // ══ CONFIG MODAL ══
                if (estado.ConfigAberto != 0)
                {
                    using (var dim = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(0,0,0,180)))
                        renderTarget.FillRectangle(new SharpDX.RectangleF(startX, startY, W, H), dim);
                    bool is30 = estado.ConfigAberto == 30;
                    bool is10m = estado.ConfigAberto == 10;
                    bool is20m = estado.ConfigAberto == 20;

                    // altura dinâmica conforme número de linhas de cada modo
                    float mw = 640f;
                    float rowGap = 76f;
                    int nLinhas = 6;              // 1.0 e 2.0 têm 6 controles; 3.0 também
                    float mh = 125f + nLinhas*rowGap + 74f;
                    float mx = startX + (W-mw)/2f, my = startY + (H-mh)/2f;
                    var modal = new SharpDX.RectangleF(mx, my, mw, mh);
                    renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = modal, RadiusX = 14f, RadiusY = 14f }, resources.BrushCardBg);
                    renderTarget.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = modal, RadiusX = 14f, RadiusY = 14f }, accent, 1.8f);

                    string tituloCfg = is30 ? "CONFIGURAR SINAL 3.0" : (is10m ? "CONFIGURAR SINAL 1.0" : "CONFIGURAR SINAL 2.0");
                    string subCfg = is30 ? "Estrat\u00E9gia institucional \u00B7 fluxo agressor em zonas de oferta/demanda"
                                   : (is10m ? "Profit Academy Regi\u00F5es"
                                   : "Estrat\u00E9gia avan\u00E7ada \u00B7 confluência de m\u00FAltiplos indicadores");
                    var corTitulo = is30 ? resources.BrushGreen : (is10m ? resources.BrushGold : resources.BrushBlue);
                    draw.DrawText(tituloCfg, resources.FontGaugeSmall, new SharpDX.RectangleF(mx+30, my+22, mw-70, 28), corTitulo);
                    draw.DrawText(subCfg, resources.FontCfgHint, new SharpDX.RectangleF(mx+30, my+52, mw-60, 18), resources.BrushTextSecondary);
                    using (var dvl = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(42,48,59,255)))
                        renderTarget.DrawLine(new SharpDX.Vector2(mx+30, my+84), new SharpDX.Vector2(mx+mw-30, my+84), dvl, 1f);
                    var xr = new SharpDX.RectangleF(mx+mw-46, my+20, 30, 30);
                    draw.DrawText("\u2715", resources.FontGaugeSmall, xr, resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                    cfgFechar = xr;

                    // reset do sistema de linhas
                    engineCfgReset();
                    float oy = my + 110;

                    // helper local: linha numérica (nome + hint enquadrado + valor + -/+)
                    System.Action<string,string,string,string> addNum = (id,nome,hint,valor) => {
                        float yy = oy + engineCfgIndex()*rowGap;
                        draw.DrawText(nome, resources.FontCfgNome, new SharpDX.RectangleF(mx+30, yy, 340, 24), resources.BrushTextPrimary);
                        draw.DrawText(hint, resources.FontCfgHint, new SharpDX.RectangleF(mx+30, yy+26, 360, 18), resources.BrushTextSecondary);
                        var minus = new SharpDX.RectangleF(mx+mw-148, yy+6, 34, 32);
                        var plus = new SharpDX.RectangleF(mx+mw-52, yy+6, 34, 32);
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = minus, RadiusX = 7f, RadiusY = 7f }, resources.BrushCardElement);
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = plus, RadiusX = 7f, RadiusY = 7f }, resources.BrushCardElement);
                        draw.DrawText("\u2212", resources.FontCfgNome, new SharpDX.RectangleF(minus.Left, minus.Top+4, minus.Width, 20), resources.BrushTextPrimary, SharpDX.DirectWrite.TextAlignment.Center);
                        draw.DrawText("+", resources.FontCfgNome, new SharpDX.RectangleF(plus.Left, plus.Top+4, plus.Width, 20), resources.BrushTextPrimary, SharpDX.DirectWrite.TextAlignment.Center);
                        draw.DrawText(valor, resources.FontCfgNome, new SharpDX.RectangleF(mx+mw-114, yy+8, 58, 24), corTitulo, SharpDX.DirectWrite.TextAlignment.Center);
                        engineCfgAdd(id, minus, plus, new SharpDX.RectangleF(0,0,0,0));
                    };
                    System.Action<string,string,string,bool> addTog = (id,nome,hint,val) => {
                        float yy = oy + engineCfgIndex()*rowGap;
                        draw.DrawText(nome, resources.FontCfgNome, new SharpDX.RectangleF(mx+30, yy, 400, 24), resources.BrushTextPrimary);
                        draw.DrawText(hint, resources.FontCfgHint, new SharpDX.RectangleF(mx+30, yy+26, 400, 18), resources.BrushTextSecondary);
                        var tg = new SharpDX.RectangleF(mx+mw-92, yy+8, 62, 30);
                        renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = tg, RadiusX = 15f, RadiusY = 15f }, val ? resources.BrushGreen : resources.BrushCardElement);
                        float kx = val ? tg.Right-17 : tg.Left+17;
                        renderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(kx, yy+23), 11f, 11f), resources.BrushWhite);
                        engineCfgAdd(id, new SharpDX.RectangleF(0,0,0,0), new SharpDX.RectangleF(0,0,0,0), tg);
                    };

                    if (is30)
                    {
                        addNum("30_agr", "Agress\u00E3o m\u00EDnima", "Percentual de fluxo comprador/vendedor exigido na zona", $"{estado.Cfg30_Agressao:F0}%");
                        addNum("30_tol", "Toler\u00E2ncia da zona", "Dist\u00E2ncia aceita da zona para entrar, medida em ATR", $"{estado.Cfg30_TolZona:F1}");
                        addNum("30_score", "Score m\u00EDnimo", "Confian\u00E7a m\u00EDnima (0-100) para o sinal ser v\u00E1lido", $"{estado.Cfg30_ScoreMin}");
                        addTog("30_inv", "Invers\u00E3o de polaridade", "Compra em zonas de demanda e vende em zonas de oferta", estado.Cfg30_Inversao);
                        addTog("30_gat", "Confirma\u00E7\u00E3o do candle", "Aguarda o candle fechar antes de emitir o sinal", estado.Cfg30_ExigirGatilho);
                        addTog("30_ctd", "Filtro de tend\u00EAncia", "Bloqueia entradas contra a dire\u00E7\u00E3o do mercado", estado.Cfg30_FiltrarContraTend);
                    }
                    else if (is10m)
                    {
                        // Sinal 1.0 — lógica FIXA: região de liquidez + delta a favor +
                        // ≥1 confluência (Estocástico / BOP / IFR). Sem pesos/score.
                        addTog("p_estoc585", "Oscilador: 5/8/5 (desmarcado = 3/5/3)", "", estado.Estoc_585);
                        addTog("plus_div", "PLUS DIVERG\u00CANCIA", "Armadilha de liquidez: rompe zona + diverg\u00EAncia RSI + delta a favor", estado.PlusDivergencia);
                    }
                    else
                    {
                        // Sinal 2.0 — tendência por estrutura de EMA (9/30) + gatilhos
                        addNum("20_score", "Score m\u00EDnimo (estrutura)", "", $"{estado.Sinal20_ScoreMin}");
                        addNum("20_slope", "Inclina\u00E7\u00E3o m\u00EDnima", "", $"{estado.Sinal20_SlopeMin}");
                        addTog("20_flip", "Gatilho: Flip de fluxo", "", estado.Sinal20_UsarFlip);
                        addTog("20_candle", "Gatilho: Candle de rejei\u00E7\u00E3o", "", estado.Sinal20_UsarCandle);
                        addTog("20_kd", "Gatilho: Cruzamento K\u00D7D", "", estado.Sinal20_UsarCruzamentoKD);
                        addTog("plus_div", "PLUS DIVERG\u00CANCIA", "", estado.PlusDivergencia);
                    }

                    var sv = new SharpDX.RectangleF(mx+30, my+mh-58, mw-60, 40);
                    renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = sv, RadiusX = 10f, RadiusY = 10f }, resources.BrushGreen);
                    draw.DrawText("SALVAR CONFIGURA\u00C7\u00C3O", resources.FontCfgNome, new SharpDX.RectangleF(sv.Left, sv.Top+10, sv.Width, 22), resources.BrushBgMain, SharpDX.DirectWrite.TextAlignment.Center);
                    cfgSalvar = sv;
                }

                // ── PAINEL DE ESTATÍSTICAS / RELATÓRIO (botão SINAIS) ──
                if (estado.StatAberto)
                {
                    float pw = 480f, ph = 340f;
                    float pxm = startX + (PanelWidth - pw) / 2f;
                    float pym = startY + 120f;
                    using (var ov = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(0,0,0,170)))
                        renderTarget.FillRectangle(new SharpDX.RectangleF(startX, startY, PanelWidth, PanelHeight), ov);
                    var card = new SharpDX.RectangleF(pxm, pym, pw, ph);
                    renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = card, RadiusX = 14f, RadiusY = 14f }, resources.BrushCardBg);
                    renderTarget.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = card, RadiusX = 14f, RadiusY = 14f }, resources.BrushBlue, 1.5f);

                    int total = StatGains + StatStops;
                    float taxa = total > 0 ? (100f * StatGains / total) : 0f;

                    draw.DrawText("RELATÓRIO DE TRADES", resources.FontCfgNome, new SharpDX.RectangleF(pxm+24, pym+20, pw-48, 26), resources.BrushBlue);

                    // taxa de acerto grande
                    var corTaxa = taxa >= 60 ? resources.BrushGreen : (taxa >= 45 ? resources.BrushGold : resources.BrushRed);
                    draw.DrawText(taxa.ToString("F1") + "%", resources.FontGaugeBig, new SharpDX.RectangleF(pxm+24, pym+58, pw-48, 56), corTaxa, SharpDX.DirectWrite.TextAlignment.Center);
                    draw.DrawText("ASSERTIVIDADE", resources.FontLabel, new SharpDX.RectangleF(pxm+24, pym+118, pw-48, 16), resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);

                    // separador
                    using (var sep = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(60,70,84,255)))
                        renderTarget.DrawLine(new SharpDX.Vector2(pxm+24, pym+146), new SharpDX.Vector2(pxm+pw-24, pym+146), sep, 1f);

                    // grade de números (3 colunas x 2 linhas)
                    float colW = (pw - 48) / 3f;
                    float gx = pxm + 24;
                    float ly1 = pym + 158, ly2 = pym + 228;
                    System.Action<float,float,string,string,SharpDX.Direct2D1.SolidColorBrush> cel = (cx,cyy,rot,val,cor) => {
                        draw.DrawText(rot, resources.FontLabel, new SharpDX.RectangleF(cx, cyy, colW, 16), resources.BrushTextSecondary, SharpDX.DirectWrite.TextAlignment.Center);
                        draw.DrawText(val, resources.FontValue, new SharpDX.RectangleF(cx, cyy+20, colW, 28), cor, SharpDX.DirectWrite.TextAlignment.Center);
                    };
                    // linha 1: total, gains, stops
                    cel(gx,           ly1, "TRADES", total.ToString(), resources.BrushTextPrimary);
                    cel(gx+colW,      ly1, "GAINS",  StatGains.ToString(), resources.BrushGreen);
                    cel(gx+colW*2,    ly1, "STOPS",  StatStops.ToString(), resources.BrushRed);

                    // separador
                    using (var sep2 = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color(60,70,84,255)))
                        renderTarget.DrawLine(new SharpDX.Vector2(pxm+24, ly2-10), new SharpDX.Vector2(pxm+pw-24, ly2-10), sep2, 1f);
                    // linha 2: compras vs vendas
                    cel(gx,           ly2, "COMPRAS", StatCompras.ToString(), resources.BrushGreen);
                    cel(gx+colW,      ly2, "VENDAS",  StatVendas.ToString(), resources.BrushRed);
                    cel(gx+colW*2,    ly2, "SINAIS",  total.ToString(), resources.BrushTextPrimary);

                    // botão fechar
                    var fech = new SharpDX.RectangleF(pxm+pw-110, pym+ph-46, 86, 30);
                    renderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = fech, RadiusX = 8f, RadiusY = 8f }, resources.BrushCardElement);
                    draw.DrawText("FECHAR", resources.FontLabel, new SharpDX.RectangleF(fech.Left, fech.Top+8, fech.Width, 16), resources.BrushTextPrimary, SharpDX.DirectWrite.TextAlignment.Center);
                    statFecharRect = fech;
                }
            }
            public SharpDX.RectangleF statFecharRect = new SharpDX.RectangleF(0,0,0,0);

            // ── sistema genérico de linhas de config ──
            private int _cfgIdx = 0;
            public void engineCfgReset() { _cfgIdx = 0; cfgLinhaCount = 0; }
            public int engineCfgIndex() { return _cfgIdx; }
            public void engineCfgAdd(string id, SharpDX.RectangleF menos, SharpDX.RectangleF mais, SharpDX.RectangleF toggle)
            {
                if (_cfgIdx >= 8) return;
                cfgLinhaId[_cfgIdx] = id;
                cfgLinhaMenos[_cfgIdx] = menos;
                cfgLinhaMais[_cfgIdx] = mais;
                cfgLinhaToggle[_cfgIdx] = toggle;
                _cfgIdx++;
                cfgLinhaCount = _cfgIdx;
            }

            public void Dispose()
            {
                resources?.Dispose();
            }
        }
        #endregion
    }

    // ============================================================================
    // JANELA FLUTUANTE — hospeda o MESMO DashboardEngine usado no gráfico, agora
    // renderizando via Direct2D diretamente no HWND da janela WPF (WindowRenderTarget).
    // Resultado: visual IDÊNTICO ao dashboard do gráfico (gauge com glow, partículas
    // direcionais, cards técnicos com barra segmentada, IA com rede neural, sparkline
    // etc.), porque é literalmente o mesmo método de desenho. Independente do SO,
    // livre entre todos os monitores, sempre no topo, redimensionável.
    // ============================================================================
    public class PainelFlutuante : System.Windows.Window, IDisposable
    {
        private SINAIS.DashboardEngine engine;
        // Estado da instância (vem do engine — cada janela tem o seu).
        private DashboardEstado estado;
        private SharpDX.Direct2D1.Factory d2dFactory;
        private SharpDX.Direct2D1.WindowRenderTarget renderTarget;
        private System.Windows.Threading.DispatcherTimer renderTimer;
        private float mouseX = 0f, mouseY = 0f;

        public PainelFlutuante(SINAIS.DashboardEngine dashboardEngine)
        {
            engine = dashboardEngine;
            estado = dashboardEngine.estado;

            WindowStyle = System.Windows.WindowStyle.None;
            AllowsTransparency = false; // Direct2D no HWND não suporta janela transparente
            Background = System.Windows.Media.Brushes.Black;
            Topmost = true;
            ShowInTaskbar = true;                 // aparece na barra p/ facilitar achar
            Title = "PROFIT ACADEMY PRO";
            ResizeMode = System.Windows.ResizeMode.CanResize;
            // O painel Direct2D desenha em pixels fixos (PanelWidth x PanelHeight). Ajustamos
            // a janela WPF (em unidades independentes de DPI) no SourceInitialized, quando o
            // fator de DPI já é conhecido, para o painel preencher a janela exatamente.
            Width = SINAIS.DashboardEngine.PanelWidth;
            Height = SINAIS.DashboardEngine.PanelHeight;
            MinWidth = 400; MinHeight = 400;
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            Left = 300; Top = 200;

            // Desacopla totalmente da janela do NinjaTrader: sem Owner, o Windows trata
            // como janela top-level independente, livre para ir a qualquer monitor.
            Owner = null;

            Content = new System.Windows.Controls.Grid { Background = System.Windows.Media.Brushes.Transparent };

            MouseMove += (s, e) =>
            {
                var pt = e.GetPosition(this);
                mouseX = (float)pt.X;
                mouseY = (float)pt.Y;
            };
            MouseLeftButtonDown += (s, e) =>
            {
                try
                {
                    var pt = e.GetPosition(this);
                    // Clique em FULL → completo (janela grande)
                    if (engine != null && engine.botaoFullRect.Width > 0 &&
                        pt.X >= engine.botaoFullRect.Left && pt.X <= engine.botaoFullRect.Right &&
                        pt.Y >= engine.botaoFullRect.Top && pt.Y <= engine.botaoFullRect.Bottom)
                    {
                        estado.ModoFull = true;
                        Width = SINAIS.DashboardEngine.PanelWidth + 16;
                        Height = SINAIS.DashboardEngine.PanelHeight + 16;
                        return;
                    }
                    // Clique em BASIC → minimalista (janela compacta)
                    if (engine != null && engine.botaoBasicRect.Width > 0 &&
                        pt.X >= engine.botaoBasicRect.Left && pt.X <= engine.botaoBasicRect.Right &&
                        pt.Y >= engine.botaoBasicRect.Top && pt.Y <= engine.botaoBasicRect.Bottom)
                    {
                        estado.ModoFull = false;
                        Width = 376; Height = 306;
                        return;
                    }
                    // Clique em SINAIS → abre/fecha o painel de estatísticas
                    if (engine != null && engine.botaoSinaisRect.Width > 0 &&
                        pt.X >= engine.botaoSinaisRect.Left && pt.X <= engine.botaoSinaisRect.Right &&
                        pt.Y >= engine.botaoSinaisRect.Top && pt.Y <= engine.botaoSinaisRect.Bottom)
                    {
                        estado.StatAberto = !estado.StatAberto;
                        return;
                    }
                    // Clique no botão MODO (Conservador ↔ Agressivo)
                    if (engine != null && engine.botaoModoRect.Width > 0 &&
                        pt.X >= engine.botaoModoRect.Left && pt.X <= engine.botaoModoRect.Right &&
                        pt.Y >= engine.botaoModoRect.Top && pt.Y <= engine.botaoModoRect.Bottom)
                    {
                        estado.ModoConservador = !estado.ModoConservador;
                        estado.ModoMudou = true;   // o OnBarUpdate reprocessa na thread certa
                        return;
                    }
                    // Clique no X → FECHA o dashboard de vez (persiste no F5). Os sinais
                    // plotados no gráfico permanecem; só o painel some.
                    if (engine != null && engine.botaoFecharDashRect.Width > 0 &&
                        pt.X >= engine.botaoFecharDashRect.Left && pt.X <= engine.botaoFecharDashRect.Right &&
                        pt.Y >= engine.botaoFecharDashRect.Top && pt.Y <= engine.botaoFecharDashRect.Bottom)
                    {
                        estado.DashboardVisivel = false;   // não reabrir no F5
                        try { this.Close(); } catch { try { this.Hide(); } catch { } }
                        return;
                    }
                    // Clique no − → MINIMIZA a janela do painel
                    if (engine != null && engine.botaoMinimizarRect.Width > 0 &&
                        pt.X >= engine.botaoMinimizarRect.Left && pt.X <= engine.botaoMinimizarRect.Right &&
                        pt.Y >= engine.botaoMinimizarRect.Top && pt.Y <= engine.botaoMinimizarRect.Bottom)
                    {
                        try { this.WindowState = System.Windows.WindowState.Minimized; } catch { }
                        return;
                    }
                    // Clique em FECHAR do painel de estatísticas
                    if (engine != null && estado.StatAberto && engine.statFecharRect.Width > 0 &&
                        pt.X >= engine.statFecharRect.Left && pt.X <= engine.statFecharRect.Right &&
                        pt.Y >= engine.statFecharRect.Top && pt.Y <= engine.statFecharRect.Bottom)
                    {
                        estado.StatAberto = false;
                        return;
                    }
                    // ── PAINEL DE CONFIG ABERTO: trata cliques do modal primeiro ──
                    if (engine != null && estado.ConfigAberto != 0)
                    {
                        System.Func<SharpDX.RectangleF,bool> hit = (r) => r.Width > 0 && pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
                        if (hit(engine.cfgFechar)) { estado.ConfigAberto = 0; return; }
                        if (hit(engine.cfgSalvar)) { estado.ConfigAberto = 0; return; }
                        // percorre as linhas do modal e aplica pela ID
                        for (int li = 0; li < engine.cfgLinhaCount; li++)
                        {
                            string id = engine.cfgLinhaId[li];
                            bool cMenos = hit(engine.cfgLinhaMenos[li]);
                            bool cMais = hit(engine.cfgLinhaMais[li]);
                            bool cTog = hit(engine.cfgLinhaToggle[li]);
                            if (!cMenos && !cMais && !cTog) continue;
                            int d = cMais ? 1 : -1;
                            switch (id)
                            {
                                // Sinal 3.0
                                case "30_agr": estado.Cfg30_Agressao = Math.Max(5, Math.Min(100, estado.Cfg30_Agressao + d*5)); break;
                                case "30_tol": estado.Cfg30_TolZona = Math.Max(0.1, Math.Min(3.0, estado.Cfg30_TolZona + d*0.1)); break;
                                case "30_score": estado.Cfg30_ScoreMin = Math.Max(0, Math.Min(200, estado.Cfg30_ScoreMin + d*5)); break;
                                case "30_inv": estado.Cfg30_Inversao = !estado.Cfg30_Inversao; break;
                                case "30_gat": estado.Cfg30_ExigirGatilho = !estado.Cfg30_ExigirGatilho; break;
                                case "30_ctd": estado.Cfg30_FiltrarContraTend = !estado.Cfg30_FiltrarContraTend; break;
                                // Sinal 2.0 — tendência por estrutura de EMA
                                case "20_score": estado.Sinal20_ScoreMin = Math.Max(0, Math.Min(100, estado.Sinal20_ScoreMin + d*5)); break;
                                case "20_slope": estado.Sinal20_SlopeMin = Math.Max(1, Math.Min(50, estado.Sinal20_SlopeMin + d)); break;
                                case "20_flip": estado.Sinal20_UsarFlip = !estado.Sinal20_UsarFlip; break;
                                case "20_candle": estado.Sinal20_UsarCandle = !estado.Sinal20_UsarCandle; break;
                                case "20_kd": estado.Sinal20_UsarCruzamentoKD = !estado.Sinal20_UsarCruzamentoKD; break;
                                case "20_anctol": estado.Sinal20_AncoragemTol = Math.Max(1, Math.Min(20, estado.Sinal20_AncoragemTol + d)); break;
                                case "20_ancbar": estado.Sinal20_AncoragemBarras = Math.Max(1, Math.Min(20, estado.Sinal20_AncoragemBarras + d)); break;
                                // Sinal 1.0
                                case "10_score": estado.Cfg10_ScoreMin = Math.Max(0, Math.Min(200, estado.Cfg10_ScoreMin + d*5)); break;
                                case "10_ema": estado.Cfg10_EmaPeriodo = Math.Max(2, Math.Min(200, estado.Cfg10_EmaPeriodo + d)); break;
                                case "10_swing": estado.Cfg10_SwingStrength = Math.Max(1, Math.Min(20, estado.Cfg10_SwingStrength + d)); break;
                                case "10_tend": estado.Cfg10_ApenasTendencia = !estado.Cfg10_ApenasTendencia; break;
                                // Sinal 1.0 — ESTRATÉGIA PADRÃO (pesos configuráveis)
                                case "p_estrut": estado.Peso_Estrutura = Math.Max(0, Math.Min(100, estado.Peso_Estrutura + d*5)); break;
                                case "p_estoc": estado.Peso_Estocastico = Math.Max(0, Math.Min(100, estado.Peso_Estocastico + d*5)); break;
                                case "p_fluxo": estado.Peso_Fluxo = Math.Max(0, Math.Min(100, estado.Peso_Fluxo + d*5)); break;
                                case "p_score": estado.Padrao_ScoreMin = Math.Max(0, Math.Min(100, estado.Padrao_ScoreMin + d*5)); break;
                                case "p_estoc585": estado.Estoc_585 = !estado.Estoc_585; break;
                                // PLUS DIVERGÊNCIA — vale para 1.0 e 2.0; reprocessa os sinais
                                case "plus_div":
                                    estado.PlusDivergencia = !estado.PlusDivergencia;
                                    estado.ModoMudou = true;   // OnBarUpdate reprocessa na thread certa
                                    break;
                                // Gerais
                                case "g_auto": estado.CfgGeral_AutoRegular = !estado.CfgGeral_AutoRegular; break;
                                case "g_anim": estado.CfgGeral_Animacoes = !estado.CfgGeral_Animacoes; break;
                            }
                            return;
                        }
                        // clique fora dos controles = ignora (mantém modal)
                        return;
                    }
                    // Engrenagens de config
                    if (engine != null && engine.cfgGear30.Width > 0 && pt.X >= engine.cfgGear30.Left && pt.X <= engine.cfgGear30.Right && pt.Y >= engine.cfgGear30.Top && pt.Y <= engine.cfgGear30.Bottom)
                    { estado.ConfigAberto = 30; return; }
                    if (engine != null && engine.cfgGear20.Width > 0 && pt.X >= engine.cfgGear20.Left && pt.X <= engine.cfgGear20.Right && pt.Y >= engine.cfgGear20.Top && pt.Y <= engine.cfgGear20.Bottom)
                    { estado.ConfigAberto = 20; return; }
                    if (engine != null && engine.cfgGear10.Width > 0 && pt.X >= engine.cfgGear10.Left && pt.X <= engine.cfgGear10.Right && pt.Y >= engine.cfgGear10.Top && pt.Y <= engine.cfgGear10.Bottom)
                    { estado.ConfigAberto = 10; return; }
                    // Clique em SINAL 1.0 → ativa 1.0 e desliga o 2.0
                    if (engine != null && engine.botaoSinal10Rect.Width > 0 &&
                        pt.X >= engine.botaoSinal10Rect.Left && pt.X <= engine.botaoSinal10Rect.Right &&
                        pt.Y >= engine.botaoSinal10Rect.Top && pt.Y <= engine.botaoSinal10Rect.Bottom)
                    {
                        if (estado.Sinal20)   // só reage se estava no 2.0
                        {
                            estado.Sinal10 = true;
                            estado.Sinal20 = false;
                            estado.Sinal20Mudou = true;   // OnBarUpdate reprocessa
                        }
                        return;
                    }
                    // Clique em SINAL 3.0
                    if (engine != null && engine.botaoSinal30Rect.Width > 0 &&
                        pt.X >= engine.botaoSinal30Rect.Left && pt.X <= engine.botaoSinal30Rect.Right &&
                        pt.Y >= engine.botaoSinal30Rect.Top && pt.Y <= engine.botaoSinal30Rect.Bottom)
                    {
                        estado.Sinal30 = !estado.Sinal30; estado.Sinal30Mudou = true;
                        if (estado.Sinal30) { estado.Sinal20 = false; estado.Sinal20Mudou = true; estado.Sinal10 = false; estado.Sinal40 = false; estado.Sinal40Mudou = true; }
                        return;
                    }
                    // Clique em SINAL 2.0 → ativa o 2.0 (tendência EMA) e desliga o 1.0
                    if (engine != null && engine.botaoSinal20Rect.Width > 0 &&
                        pt.X >= engine.botaoSinal20Rect.Left && pt.X <= engine.botaoSinal20Rect.Right &&
                        pt.Y >= engine.botaoSinal20Rect.Top && pt.Y <= engine.botaoSinal20Rect.Bottom)
                    {
                        if (!estado.Sinal20)   // só reage se estava no 1.0
                        {
                            estado.Sinal20 = true; estado.Sinal20Mudou = true;   // OnBarUpdate reprocessa
                            estado.Sinal10 = false;
                            estado.Sinal30 = false; estado.Sinal40 = false;
                        }
                        return;
                    }
                    // Clique em SINAL 4.0 → estratégia FLIP institucional
                    if (engine != null && engine.botaoSinal40Rect.Width > 0 &&
                        pt.X >= engine.botaoSinal40Rect.Left && pt.X <= engine.botaoSinal40Rect.Right &&
                        pt.Y >= engine.botaoSinal40Rect.Top && pt.Y <= engine.botaoSinal40Rect.Bottom)
                    {
                        estado.Sinal40 = !estado.Sinal40; estado.Sinal40Mudou = true;
                        if (estado.Sinal40) { estado.Sinal20 = false; estado.Sinal20Mudou = true; estado.Sinal30 = false; estado.Sinal30Mudou = true; estado.Sinal10 = false; estado.Sinal10Mudou = true; }
                        return;
                    }
                    DragMove();
                }
                catch { }
            };

            SourceInitialized += OnSourceInitialized;
            SizeChanged += (s, e) => ResizeRenderTarget();
            Closed += (s, e) => Dispose();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                // Garante que o owner nativo (HWND) seja nulo — sem isso, a WPF pode
                // herdar a janela ativa (NinjaTrader) como dona e ficar presa a ela.
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                helper.Owner = IntPtr.Zero;
                IntPtr hwnd = helper.Handle;

                // Ajusta o tamanho da janela para que PanelWidth×PanelHeight pixels do
                // painel Direct2D preencham a janela exatamente (compensa o DPI do Windows).
                try
                {
                    var src = System.Windows.PresentationSource.FromVisual(this);
                    if (src != null && src.CompositionTarget != null)
                    {
                        double dpiX = src.CompositionTarget.TransformToDevice.M11;
                        double dpiY = src.CompositionTarget.TransformToDevice.M22;
                        if (dpiX > 0 && dpiY > 0)
                        {
                            Width = SINAIS.DashboardEngine.PanelWidth / dpiX;
                            Height = SINAIS.DashboardEngine.PanelHeight / dpiY;
                        }
                    }
                }
                catch { }

                d2dFactory = new SharpDX.Direct2D1.Factory(SharpDX.Direct2D1.FactoryType.SingleThreaded);
                CreateRenderTarget(hwnd);

                // Timer de renderização (~30 fps) — redesenha o painel continuamente,
                // igual ao animTimer do dashboard do gráfico.
                renderTimer = new System.Windows.Threading.DispatcherTimer();
                renderTimer.Interval = TimeSpan.FromMilliseconds(120);  // ~8fps: dashboard não precisa de 30fps, alivia CPU/GPU
                renderTimer.Tick += (s2, e2) => RenderFrame();
                renderTimer.Start();
            }
            catch { }
        }

        private void CreateRenderTarget(IntPtr hwnd)
        {
            // ActualWidth/Height são unidades WPF (independentes de DPI). O HWND Direct2D
            // desenha em PIXELS reais. Convertemos usando o fator de DPI para o painel
            // preencher a janela inteira mesmo com escala do Windows a 125%/150%.
            double dpiX = 1.0, dpiY = 1.0;
            try
            {
                var src = System.Windows.PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                {
                    dpiX = src.CompositionTarget.TransformToDevice.M11;
                    dpiY = src.CompositionTarget.TransformToDevice.M22;
                }
            }
            catch { }

            int w = Math.Max(50, (int)(ActualWidth * dpiX));
            int h = Math.Max(50, (int)(ActualHeight * dpiY));

            var hwndProps = new SharpDX.Direct2D1.HwndRenderTargetProperties
            {
                Hwnd = hwnd,
                PixelSize = new SharpDX.Size2(w, h),
                PresentOptions = SharpDX.Direct2D1.PresentOptions.None
            };
            // Construtor padrão: deixa o Direct2D escolher o pixel format automaticamente.
            // Evita referenciar SharpDX.DXGI.Format diretamente (assembly não referenciada
            // por padrão no ambiente do NinjaScript, causa CS0234/CS0012 ao compilar).
            var rtProps = new SharpDX.Direct2D1.RenderTargetProperties();

            try { renderTarget?.Dispose(); } catch { }
            renderTarget = new SharpDX.Direct2D1.WindowRenderTarget(d2dFactory, rtProps, hwndProps);
        }

        private void ResizeRenderTarget()
        {
            try
            {
                if (renderTarget == null) return;
                double dpiX = 1.0, dpiY = 1.0;
                try
                {
                    var src = System.Windows.PresentationSource.FromVisual(this);
                    if (src != null && src.CompositionTarget != null)
                    {
                        dpiX = src.CompositionTarget.TransformToDevice.M11;
                        dpiY = src.CompositionTarget.TransformToDevice.M22;
                    }
                }
                catch { }
                int w = Math.Max(50, (int)(ActualWidth * dpiX));
                int h = Math.Max(50, (int)(ActualHeight * dpiY));
                renderTarget.Resize(new SharpDX.Size2(w, h));
            }
            catch { }
        }

        private volatile bool _parado = false;

        // Para o render com segurança antes do engine ser destruído (evita crash ao recarregar).
        public void PararRender()
        {
            _parado = true;
            try { if (renderTimer != null) { renderTimer.Stop(); renderTimer = null; } } catch { }
            engine = null;  // solta a referência ao engine compartilhado (será liberado pelo indicador)
        }

        private void RenderFrame()
        {
            if (_parado || renderTarget == null || engine == null) return;
            // Se o usuário fechou o dashboard (X) ou desativou nas opções, encerra a janela.
            if (estado != null && !estado.DashboardVisivel)
            {
                try { this.Close(); } catch { }
                return;
            }
            // Não desenha se a janela está minimizada ou invisível (economiza recursos).
            if (WindowState == System.Windows.WindowState.Minimized || !IsVisible) return;
            try
            {
                renderTarget.BeginDraw();
                renderTarget.Clear(new SharpDX.Color(11, 14, 19));
                engine.Render(renderTarget, 8f, 8f, mouseX, mouseY);
                renderTarget.EndDraw();
            }
            catch
            {
                // Recuperação simples se o device D3D for perdido (ex.: troca de GPU/driver).
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    CreateRenderTarget(helper.Handle);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            try { renderTimer?.Stop(); renderTimer = null; } catch { }
            try { renderTarget?.Dispose(); renderTarget = null; } catch { }
            try { d2dFactory?.Dispose(); d2dFactory = null; } catch { }
        }
    }
}


