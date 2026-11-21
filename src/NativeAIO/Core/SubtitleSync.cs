// Recalage automatique des sous-titres, repris de la version web (lib/subtitles/align.ts et
// auto-sync.ts). Les répliques d'une piste du fichier (calée sur la vidéo) servent de
// référence : on cherche le décalage, entre -10 et +10 min, qui fait le mieux coïncider les
// moments de parole des deux pistes. Seuls les temps comptent, les deux pistes peuvent être
// dans des langues différentes. Un fichier fait pour une autre cadence d'image (25 contre
// 23,976 images par seconde) est reconnu à son rapport de vitesse.
//
// Différence avec la version web : les deux courbes de calcul (près d'1 Mo chacune) sont
// créées une fois par recalage et réutilisées, car un gros tableau neuf à chaque essai
// déclenche sur la TV des ramasse-miettes complets qui figent l'écran (LECONS.md).
using System;
using System.Collections.Generic;

namespace NativeAIO.Core
{
    // Temps d'une réplique à l'écran, c'est-à-dire de parole.
    struct Span
    {
        public double Start, End;
        public Span(double s, double e) { Start = s; End = e; }
    }

    class Alignment
    {
        public int Delay;       // décalage qui cale la cible sur la référence
        public double Ratio;    // la cible va à ce rapport de la référence
        public bool Confident;  // nettement meilleur que tout autre décalage
        public double Match, Lead; // part de parole commune, avance sur le décalage suivant (journal)
    }

    static class Align
    {
        const double LongestMs = 12000;     // plus long : panneaux ou chansons, pas de la parole
        const double UnreadGapMs = 60000;   // trou plus long : passage non lu de la référence
        public const int StepMs = 10;
        const double NearMs = 3000;
        const double MinMatch = 0.5, MinLead = 1.6, EdgeMs = 150, MinEdgeLead = 2, AgreeMs = 300;
        const double Film = 24000.0 / 1001;
        static readonly double[] Ratios = { 1, 25 / Film, Film / 25 };

        static List<Span> Speech(List<Span> lines, double ratio = 1)
        {
            var sorted = new List<Span>();
            foreach (Span l in lines)
                if (l.End > l.Start && l.End - l.Start <= LongestMs) sorted.Add(new Span(l.Start * ratio, l.End * ratio));
            sorted.Sort((a, b) => a.Start.CompareTo(b.Start));
            return Join(sorted, 0);
        }

        static List<Span> Join(List<Span> spans, double gap)
        {
            var joined = new List<Span>();
            foreach (Span s in spans)
            {
                int n = joined.Count;
                if (n > 0 && s.Start - joined[n - 1].End <= gap)
                {
                    Span last = joined[n - 1];
                    last.End = Math.Max(last.End, s.End);
                    joined[n - 1] = last;
                }
                else joined.Add(s);
            }
            return joined;
        }

        static void Edges(List<Span> lines, double ratio, out List<double> starts, out List<double> ends)
        {
            starts = new List<double>();
            ends = new List<double>();
            foreach (Span l in lines)
                if (l.End > l.Start && l.End - l.Start <= LongestMs) { starts.Add(l.Start * ratio); ends.Add(l.End * ratio); }
            starts.Sort();
            ends.Sort();
        }

        // Courbe sur les décalages de -limite à +limite, construite par ses changements de pente.
        public class Curve
        {
            public readonly double[] Values;
            public readonly int Limit;
            double slope, value;

            public Curve(int limit)
            {
                Limit = limit;
                Values = new double[(int)Math.Round(2.0 * limit / StepMs) + 1];
            }

            public void Clear()
            {
                Array.Clear(Values, 0, Values.Length);
                slope = value = 0;
            }

            public void Add(double delay, double change)
            {
                int i = (int)Math.Round((delay + Limit) / StepMs);
                if (i >= Values.Length) return;
                if (i >= 0) Values[i] += change;
                else { slope += change; value -= change * i * StepMs; }
            }

            public double[] Sum()
            {
                double s = slope, v = value;
                for (int i = 0; i < Values.Length; i++)
                {
                    double change = Values[i];
                    Values[i] = v;
                    s += change;
                    v += s * StepMs;
                }
                return Values;
            }
        }

        // Durée de recouvrement de target, décalée de chaque valeur, avec fixed : chaque paire
        // de répliques se recouvre sur un trapèze de décalages, ajouté par ses 4 changements de pente.
        static double[] Overlaps(List<Span> fix, List<Span> target, Curve curve)
        {
            curve.Clear();
            int limit = curve.Limit, lo = 0, hi = 0;
            foreach (Span t in target)
            {
                while (lo < fix.Count && fix[lo].End - t.Start <= -limit) lo++;
                while (hi < fix.Count && fix[hi].Start - t.End < limit) hi++;
                for (int i = lo; i < hi; i++)
                {
                    Span f = fix[i];
                    double first = f.Start - t.End, last = f.End - t.Start;
                    double full = Math.Min(f.End - f.Start, t.End - t.Start);
                    curve.Add(first, 1);
                    curve.Add(first + full, -1);
                    curve.Add(last - full, -1);
                    curve.Add(last, 1);
                }
            }
            return curve.Sum();
        }

        // Nombre de moments de target que chaque décalage amène près d'un moment de fixed.
        static double[] Meetings(List<double> fix, List<double> target, Curve curve)
        {
            curve.Clear();
            int limit = curve.Limit, lo = 0, hi = 0;
            foreach (double t in target)
            {
                while (lo < fix.Count && fix[lo] - t < -limit - EdgeMs) lo++;
                while (hi < fix.Count && fix[hi] - t <= limit + EdgeMs) hi++;
                for (int i = lo; i < hi; i++)
                {
                    double d = fix[i] - t;
                    curve.Add(d - EdgeMs, 1 / EdgeMs);
                    curve.Add(d, -2 / EdgeMs);
                    curve.Add(d + EdgeMs, 1 / EdgeMs);
                }
            }
            return curve.Sum();
        }

        // Meilleur décalage d'une courbe, sa valeur, et son avance sur le meilleur hors de ses abords.
        static void Peak(double[] c, int limit, out double delay, out double top, out double lead)
        {
            int best = 0;
            double sum = 0;
            for (int i = 0; i < c.Length; i++) { sum += c[i]; if (c[i] > c[best]) best = i; }
            top = c[best];
            int left = best, right = best;
            while (left > 0 && c[left - 1] >= top * 0.99) left--;
            while (right < c.Length - 1 && c[right + 1] >= top * 0.99) right++;
            double next = 0;
            int near = (int)(NearMs / StepMs);
            for (int i = 0; i < c.Length; i++)
                if (Math.Abs(i - best) > near) next = Math.Max(next, c[i]);
            double chance = sum / c.Length;
            delay = (left + right) / 2.0 * StepMs - limit;
            lead = next > chance ? (top - chance) / (next - chance) : double.PositiveInfinity;
        }

        class Match
        {
            public double Delay, Ratio, Value, Lead;
        }

        static Match Score(List<Span> heard, List<Span> read, List<Span> target, Curve a, Curve b)
        {
            double[] both = Overlaps(heard, target, a);
            double[] shown = Overlaps(read, target, b);
            double spoken = 0;
            foreach (Span s in heard) spoken += s.End - s.Start;
            for (int i = 0; i < both.Length; i++)
            {
                double d = spoken + shown[i] - both[i];
                both[i] /= d == 0 ? 1 : d;
            }
            Peak(both, a.Limit, out double delay, out double top, out double lead);
            return new Match { Delay = delay, Value = top, Lead = lead };
        }

        // Dialogues denses : des décalages éloignés de quelques secondes se recouvrent presque
        // aussi bien ; les moments où les répliques commencent et finissent les départagent.
        static bool EdgesAgree(List<Span> reference, List<Span> target, Match found, double from, double to, Curve a, Curve b)
        {
            Edges(reference, 1, out List<double> fs, out List<double> fe);
            Edges(target, found.Ratio, out List<double> ms, out List<double> me);
            List<double> Inside(List<double> times) => times.FindAll(t => t > from && t < to);
            double[] starts = Meetings(fs, Inside(ms), a);
            double[] ends = Meetings(fe, Inside(me), b);
            for (int i = 0; i < starts.Length; i++) starts[i] += ends[i];
            Peak(starts, a.Limit, out double delay, out _, out double lead);
            return lead >= MinEdgeLead && Math.Abs(delay - found.Delay) <= AgreeMs;
        }

        // Décalage (dans la limite des courbes) qui cale target sur reference ; reference peut ne
        // couvrir que des parties de la vidéo.
        public static Alignment Run(List<Span> reference, List<Span> target, Curve a, Curve b)
        {
            List<Span> heard = Speech(reference);
            if (heard.Count == 0) return null;
            List<Span> read = Join(heard, UnreadGapMs);
            int limit = a.Limit;
            double from = heard[0].Start - limit, to = heard[heard.Count - 1].End + limit;
            Match found = null;
            foreach (double ratio in Ratios)
            {
                List<Span> lines = Speech(target, ratio).FindAll(s => s.End > from && s.Start < to);
                if (lines.Count == 0) continue;
                Match m = Score(heard, read, lines, a, b);
                m.Ratio = ratio;
                if (found == null || m.Value > found.Value) found = m;
            }
            if (found == null) return null;
            bool confident = found.Value >= MinMatch && (found.Lead >= MinLead || EdgesAgree(reference, target, found, from, to, a, b));
            return new Alignment { Delay = (int)Math.Round(found.Delay), Ratio = found.Ratio, Confident = confident, Match = found.Value, Lead = found.Lead };
        }
    }

    // Résultat d'un essai : un décalage, un rapport de vitesse, ou l'échec.
    class SyncResult
    {
        public int? Delay;
        public double? Ratio;
        public bool Unmatched;
    }

    // Cale les répliques d'une piste ajoutée sur celles de la référence au fur et à mesure
    // qu'elles arrivent : réponse quand deux essais concordent ou qu'un seul a assez de
    // répliques, puis suivi des répliques autour de la position tant que la lecture continue.
    class SubtitleSync
    {
        const int MinLines = 30, MinSpanMs = 60000, WindowLines = 100, GiveUpLines = 150, EveryLines = 10, SureLines = 60;
        const int AgreeMs = 300, NudgeMs = 150;

        readonly Dictionary<string, Span> heard = new Dictionary<string, Span>();
        readonly List<Span> target;
        readonly Align.Curve a, b;
        readonly int patience;
        int received, triedAt;
        Alignment last;
        int? applied;
        public bool Done;
        // Dernier essai, pour le journal de la sonde ; Tries : nombre d'essais de calage faits.
        public string LastTry;
        public int Tries;

        // asked : demandé par l'utilisateur, qui attend la réponse (elle vient dès qu'un essai suffit).
        // a et b : courbes de calcul fournies par l'appelant, réutilisées d'un recalage à l'autre
        // (jamais deux essais en même temps sur les mêmes courbes).
        public SubtitleSync(List<Span> target, Align.Curve a, Align.Curve b, bool asked)
        {
            this.target = target;
            this.a = a;
            this.b = b;
            patience = asked ? SureLines : GiveUpLines;
        }

        // Répliques de la référence reçues ; un résultat quand elles le fixent ou le déplacent.
        public SyncResult Add(List<Span> lines, int positionMs)
        {
            if (Done) return null;
            foreach (Span l in lines)
                heard[(int)Math.Floor(l.Start / 10) + ":" + (int)Math.Floor(l.End / 10)] = l;
            received += lines.Count;
            int count = heard.Count;
            if (count < MinLines || received - triedAt < EveryLines) return null;
            List<Span> reference = Around(positionMs);
            if (reference[reference.Count - 1].End - reference[0].Start < MinSpanMs) return null;
            triedAt = received;
            Alignment found = Align.Run(reference, target, a, b);
            Tries++;
            LastTry = found == null ? $"essai {Tries} sur {reference.Count} répliques : rien à comparer"
                : $"essai {Tries} sur {reference.Count} répliques : {found.Delay} ms, rapport {found.Ratio:0.0000}, part commune {found.Match:0.00}, avance {found.Lead:0.0}" + (found.Confident ? ", sûr" : ", pas sûr");
            Alignment prev = last;
            last = found;
            if (found == null || !found.Confident)
            {
                if (applied != null || count < patience) return null;
                Done = true;
                return new SyncResult { Unmatched = true };
            }
            bool agrees = prev != null && prev.Confident && prev.Ratio == found.Ratio && Math.Abs(prev.Delay - found.Delay) <= AgreeMs;
            if (!agrees && (applied != null || reference.Count < SureLines)) return null;
            if (found.Ratio != 1)
            {
                if (applied != null) return null;
                Done = true;
                return new SyncResult { Ratio = found.Ratio };
            }
            if (applied != null && Math.Abs(found.Delay - applied.Value) < NudgeMs) return null;
            applied = found.Delay;
            return new SyncResult { Delay = found.Delay };
        }

        List<Span> Around(int at)
        {
            var lines = new List<Span>(heard.Values);
            lines.Sort((x, y) => x.Start.CompareTo(y.Start));
            int next = lines.FindIndex(l => l.Start >= at);
            int from = Math.Max(0, Math.Min((next < 0 ? lines.Count : next) - WindowLines / 2, lines.Count - WindowLines));
            return lines.GetRange(from, Math.Min(WindowLines, lines.Count - from));
        }
    }
}
