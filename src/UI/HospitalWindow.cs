using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace AILegacy.UI
{
    /// <summary>The hospital (or village healer) window, opened from the settlement menu. Prefab: Module\GUI\Prefabs\AILegacyHospital.xml.</summary>
    internal static class HospitalWindow
    {
        public static bool Open()
        {
            var st = Settlement.CurrentSettlement;
            if (st == null || MobileParty.MainParty == null || Hero.MainHero == null || !Diseases.HospitalHere(st)) return false;
            var vm = new HospitalVM(st);
            GauntletWindow w = null;
            vm.OnClose = () => w?.Close();
            w = GauntletWindow.Open("AILegacyHospital", vm, () => w?.Close());
            return w != null;
        }

        /// <summary>Braces would be read as text variables by the game's text widgets.</summary>
        internal static string Clean(string s) => (s ?? "").Replace("{", "(").Replace("}", ")");
    }

    public abstract class HospitalVMBase : ViewModel
    {
        protected void SetS(ref string field, string value, string name)
        {
            value = value ?? "";
            if (value == field) return;
            field = value;
            OnPropertyChangedWithValue(value, name);
        }

        protected void SetB(ref bool field, bool value, string name)
        {
            if (value == field) return;
            field = value;
            OnPropertyChangedWithValue(value, name);
        }
    }

    /// <summary>One sick hero, or one sick band of soldiers or prisoners. Clicking it marks it for treatment.</summary>
    public class HospitalPatientVM : HospitalVMBase
    {
        internal readonly Sickness Case;
        internal readonly string Who;
        private readonly Action<HospitalPatientVM> _onToggle;
        private string _line1 = "", _line2 = "", _markText = "";
        private bool _isMarked, _canMark;

        internal HospitalPatientVM(Sickness s, string who, Action<HospitalPatientVM> onToggle) { Case = s; Who = who; _onToggle = onToggle; }

        public void ExecuteToggle() { if (CanMark) _onToggle?.Invoke(this); }

        [DataSourceProperty] public string Line1 { get => _line1; set => SetS(ref _line1, value, nameof(Line1)); }
        [DataSourceProperty] public string Line2 { get => _line2; set => SetS(ref _line2, value, nameof(Line2)); }
        [DataSourceProperty] public string MarkText { get => _markText; set => SetS(ref _markText, value, nameof(MarkText)); }
        [DataSourceProperty] public bool IsMarked { get => _isMarked; set => SetB(ref _isMarked, value, nameof(IsMarked)); }
        [DataSourceProperty] public bool CanMark { get => _canMark; set => SetB(ref _canMark, value, nameof(CanMark)); }
    }

    /// <summary>You, your companions, your troops or your prisoners: their wounds and their sick.</summary>
    public class HospitalSectionVM : HospitalVMBase
    {
        private readonly Action _onHeal;
        private string _title = "", _info = "", _woundButtonText = "", _emptyText = "";
        private bool _hasWoundButton, _canHealWounds, _hasEmptyText;

        internal HospitalSectionVM(Action onHeal) { _onHeal = onHeal; }

        public void ExecuteHealWounds() { if (CanHealWounds) _onHeal?.Invoke(); }

        [DataSourceProperty] public string Title { get => _title; set => SetS(ref _title, value, nameof(Title)); }
        [DataSourceProperty] public string Info { get => _info; set => SetS(ref _info, value, nameof(Info)); }
        [DataSourceProperty] public string WoundButtonText { get => _woundButtonText; set => SetS(ref _woundButtonText, value, nameof(WoundButtonText)); }
        [DataSourceProperty] public string EmptyText { get => _emptyText; set => SetS(ref _emptyText, value, nameof(EmptyText)); }
        [DataSourceProperty] public bool HasWoundButton { get => _hasWoundButton; set => SetB(ref _hasWoundButton, value, nameof(HasWoundButton)); }
        [DataSourceProperty] public bool CanHealWounds { get => _canHealWounds; set => SetB(ref _canHealWounds, value, nameof(CanHealWounds)); }
        [DataSourceProperty] public bool HasEmptyText { get => _hasEmptyText; set => SetB(ref _hasEmptyText, value, nameof(HasEmptyText)); }
        [DataSourceProperty] public MBBindingList<HospitalPatientVM> Patients { get; } = new MBBindingList<HospitalPatientVM>();
    }

    /// <summary>A way of treatment: the local care, or the local care with a herb quality.</summary>
    public class HospitalMethodVM : HospitalVMBase
    {
        private readonly Action _onSelect;
        private string _name = "", _detail = "";
        private bool _isSelected;

        internal HospitalMethodVM(string name, string detail, Action onSelect) { _name = name ?? ""; _detail = detail ?? ""; _onSelect = onSelect; }

        public void ExecuteSelect() => _onSelect?.Invoke();

        [DataSourceProperty] public string Name { get => _name; set => SetS(ref _name, value, nameof(Name)); }
        [DataSourceProperty] public string Detail { get => _detail; set => SetS(ref _detail, value, nameof(Detail)); }
        [DataSourceProperty] public bool IsSelected { get => _isSelected; set => SetB(ref _isSelected, value, nameof(IsSelected)); }
    }

    /// <summary>Prevention bought and taken on the spot.</summary>
    public class HospitalPreventionVM : HospitalVMBase
    {
        private readonly Action _onBuy;
        private string _text = "", _buttonText = "";
        private bool _canBuy;

        internal HospitalPreventionVM(Action onBuy) { _onBuy = onBuy; }

        public void ExecuteBuy() { if (CanBuy) _onBuy?.Invoke(); }

        [DataSourceProperty] public string Text { get => _text; set => SetS(ref _text, value, nameof(Text)); }
        [DataSourceProperty] public string ButtonText { get => _buttonText; set => SetS(ref _buttonText, value, nameof(ButtonText)); }
        [DataSourceProperty] public bool CanBuy { get => _canBuy; set => SetB(ref _canBuy, value, nameof(CanBuy)); }
    }

    /// <summary>
    /// The whole window. Header with the facts of the place and of your party; left, who is wounded or sick (click a
    /// patient to mark them); right, the ways of treatment, what treating the marked would cost and do, prevention to
    /// drink for the road and the diseases the physicians know of.
    /// </summary>
    public class HospitalVM : HospitalVMBase
    {
        private readonly Settlement _st;
        private List<HospitalMethod> _methods = new List<HospitalMethod>();
        private int _method;
        private readonly HashSet<Sickness> _marked = new HashSet<Sickness>();
        private readonly List<HospitalPatientVM> _patients = new List<HospitalPatientVM>();
        public Action OnClose;

        private string _title = "", _factsLeft = "", _factsRight = "", _summary = "", _status = "", _outbreaks = "", _methodNote = "";
        private bool _hasDiseases, _canTreat, _hasStatus, _hasMethodNote;

        internal HospitalVM(Settlement st)
        {
            _st = st;
            Rebuild();
        }

        // ---------------------------------------------------------------- fixed texts

        [DataSourceProperty] public string CloseText => L.T("AIL_e8438e2a", "Cerrar");
        [DataSourceProperty] public string TreatTitle => L.T("AIL_e7b1c938", "Forma de tratamiento");
        [DataSourceProperty] public string MarkAllText => L.T("AIL_e7b1c939", "Marcar a todos");
        [DataSourceProperty] public string MarkSevereText => L.T("AIL_e7b1c93a", "Solo los graves");
        [DataSourceProperty] public string ClearText => L.T("AIL_e7b1c93b", "Desmarcar");
        [DataSourceProperty] public string TreatText => L.T("AIL_e7b1c93c", "Tratar a los marcados");
        [DataSourceProperty] public string PreventionTitle => L.T("AIL_e7b1c941", "Prevención para el camino");
        [DataSourceProperty] public string OutbreaksTitle => Diseases.HospitalOutbreaksTitle;

        // ---------------------------------------------------------------- bound values

        [DataSourceProperty] public string Title { get => _title; set => SetS(ref _title, value, nameof(Title)); }
        [DataSourceProperty] public string FactsLeft { get => _factsLeft; set => SetS(ref _factsLeft, value, nameof(FactsLeft)); }
        [DataSourceProperty] public string FactsRight { get => _factsRight; set => SetS(ref _factsRight, value, nameof(FactsRight)); }
        [DataSourceProperty] public string Summary { get => _summary; set => SetS(ref _summary, value, nameof(Summary)); }
        [DataSourceProperty] public string Status { get => _status; set => SetS(ref _status, value, nameof(Status)); }
        [DataSourceProperty] public string Outbreaks { get => _outbreaks; set => SetS(ref _outbreaks, value, nameof(Outbreaks)); }
        [DataSourceProperty] public string MethodNote { get => _methodNote; set => SetS(ref _methodNote, value, nameof(MethodNote)); }
        [DataSourceProperty] public bool HasDiseases { get => _hasDiseases; set => SetB(ref _hasDiseases, value, nameof(HasDiseases)); }
        [DataSourceProperty] public bool CanTreat { get => _canTreat; set => SetB(ref _canTreat, value, nameof(CanTreat)); }
        [DataSourceProperty] public bool HasStatus { get => _hasStatus; set => SetB(ref _hasStatus, value, nameof(HasStatus)); }
        [DataSourceProperty] public bool HasMethodNote { get => _hasMethodNote; set => SetB(ref _hasMethodNote, value, nameof(HasMethodNote)); }
        [DataSourceProperty] public MBBindingList<HospitalSectionVM> Sections { get; } = new MBBindingList<HospitalSectionVM>();
        [DataSourceProperty] public MBBindingList<HospitalMethodVM> Methods { get; } = new MBBindingList<HospitalMethodVM>();
        [DataSourceProperty] public MBBindingList<HospitalPreventionVM> Preventions { get; } = new MBBindingList<HospitalPreventionVM>();

        private HospitalMethod Method => _method >= 0 && _method < _methods.Count ? _methods[_method] : null;

        // ---------------------------------------------------------------- commands

        public void ExecuteClose() => OnClose?.Invoke();

        public void ExecuteMarkAll() => Mark(p => true);

        public void ExecuteMarkSevere() => Mark(p => Diseases.HospitalSevere(p.Case));

        public void ExecuteClear() => Mark(p => false);

        public void ExecuteTreat()
        {
            Guard("treatment", () =>
            {
                var cases = _patients.Where(p => p.IsMarked && p.CanMark).Select(p => p.Case).ToList();
                var m = Method;
                if (cases.Count == 0 || m == null) return;
                int n = cases.Count, cost = cases.Sum(s => Diseases.HospitalCost(s, m, _st));
                if (Diseases.HospitalTreat(cases, m, _st))
                {
                    Status = L.F("AIL_e7b1c944", $"Los médicos empiezan el tratamiento de {n} enfermo(s) por {cost} denarios.");
                    _marked.Clear();
                }
                Rebuild();
            });
        }

        // ---------------------------------------------------------------- building

        /// <summary>Everything from scratch: after anything that changes the purse, the wounds or the sick.</summary>
        private void Rebuild()
        {
            Guard("rebuild", () =>
            {
                Title = HospitalWindow.Clean(Diseases.HospitalTitle(_st));
                FactsLeft = HospitalWindow.Clean(string.Join("\n", Diseases.HospitalFactsLeft(_st)));
                FactsRight = HospitalWindow.Clean(string.Join("\n", Diseases.HospitalFactsRight(_st)));
                HasDiseases = Diseases.Enabled;
                HasStatus = !string.IsNullOrEmpty(Status);

                _methods = Diseases.HospitalMethods(_st, out bool better);
                if (_method >= _methods.Count) _method = 0;
                MethodNote = better ? L.T("AIL_e7b1c942", "Las mejores hierbas solo llegan a las ciudades ricas y a las capitales.") : "";
                HasMethodNote = better;
                Methods.Clear();
                for (int i = 0; i < _methods.Count; i++)
                {
                    int index = i;
                    var m = _methods[i];
                    Methods.Add(new HospitalMethodVM(HospitalWindow.Clean(m.Name), HospitalWindow.Clean(m.Detail), () => SelectMethod(index)) { IsSelected = i == _method });
                }

                _patients.Clear();
                Sections.Clear();
                foreach (var sec in Diseases.HospitalSections(_st))
                {
                    int kind = sec.Kind;
                    var vm = new HospitalSectionVM(() => HealWounds(kind))
                    {
                        Title = HospitalWindow.Clean(sec.Title),
                        Info = HospitalWindow.Clean(sec.Info),
                        HasWoundButton = sec.WoundCost > 0,
                        CanHealWounds = sec.WoundCost > 0 && Hero.MainHero.Gold >= sec.WoundCost,
                        WoundButtonText = sec.WoundCost > 0 ? L.F("AIL_e7b1c936", $"Curar heridas ({sec.WoundCost})") : "",
                    };
                    foreach (var c in sec.Cases)
                    {
                        var p = new HospitalPatientVM(c.Key, c.Value, Toggle);
                        vm.Patients.Add(p);
                        _patients.Add(p);
                    }
                    vm.HasEmptyText = Diseases.Enabled && vm.Patients.Count == 0;
                    vm.EmptyText = vm.HasEmptyText ? L.T("AIL_e7b1c937", "Nadie está enfermo.") : "";
                    Sections.Add(vm);
                }
                _marked.RemoveWhere(s => !_patients.Any(p => p.Case == s) || !Diseases.HospitalCanTreat(s));
                RefreshPatients();

                Preventions.Clear();
                foreach (var pr in Diseases.HospitalPreventions(_st))
                {
                    var item = pr;
                    Preventions.Add(new HospitalPreventionVM(() => BuyPrevention(item))
                    {
                        Text = HospitalWindow.Clean(pr.Text),
                        ButtonText = HospitalWindow.Clean(pr.Button),
                        CanBuy = pr.Allowed && Hero.MainHero.Gold >= pr.Price,
                    });
                }

                Outbreaks = HospitalWindow.Clean(Diseases.HospitalOutbreaks(_st));
            });
        }

        /// <summary>Marks and the patients' second lines, which depend on the chosen way of treatment; and the totals.</summary>
        private void RefreshPatients()
        {
            var m = Method;
            foreach (var p in _patients)
            {
                bool open = Diseases.HospitalCanTreat(p.Case);
                p.CanMark = open;
                p.IsMarked = open && _marked.Contains(p.Case);
                p.MarkText = !open ? "" : p.IsMarked ? "[X]" : "[  ]";
                p.Line1 = HospitalWindow.Clean(Diseases.HospitalCaseTitle(p.Case, p.Who));
                p.Line2 = HospitalWindow.Clean(Diseases.HospitalCaseState(p.Case, m, _st));
            }
            RefreshSummary();
        }

        private void RefreshSummary()
        {
            var m = Method;
            var marked = _patients.Where(p => p.IsMarked && p.CanMark).Select(p => p.Case).ToList();
            if (!Diseases.Enabled) { Summary = HospitalWindow.Clean(Diseases.HospitalOutbreaks(_st)); CanTreat = false; return; }
            if (marked.Count == 0 || m == null)
            {
                Summary = L.T("AIL_e7b1c93d", "Marca a los enfermos que quieras tratar. Los que ya están en tratamiento no se pueden marcar.");
                CanTreat = false;
                return;
            }
            int n = marked.Count, cost = marked.Sum(s => Diseases.HospitalCost(s, m, _st)), gold = Hero.MainHero.Gold;
            var chances = marked.Select(s => Diseases.HospitalChance(s, m)).ToList();
            int lo = chances.Min(), hi = chances.Max();
            string chance = lo == hi ? lo + " %" : lo + "–" + hi + " %";
            string text = L.F("AIL_e7b1c93e", $"Marcados: {n}. Mejora diaria: {chance}. Precio total: {cost} denarios (tienes {gold}).");
            if (cost > gold)
            {
                int lack = cost - gold;
                text += " " + (Diseases.HospitalCanPay(cost)
                    ? L.F("AIL_e7b1c940", $"Te faltan {lack} denarios: los médicos apuntarán el resto a la cuenta de tu clan.")
                    : L.F("AIL_e7b1c93f", $"Te faltan {lack} denarios."));
            }
            Summary = HospitalWindow.Clean(text);
            CanTreat = Diseases.HospitalCanPay(cost);
        }

        private void Toggle(HospitalPatientVM p)
        {
            Guard("mark", () =>
            {
                if (!_marked.Remove(p.Case)) _marked.Add(p.Case);
                RefreshPatients();
            });
        }

        private void Mark(Func<HospitalPatientVM, bool> which)
        {
            Guard("mark", () =>
            {
                _marked.Clear();
                foreach (var p in _patients)
                    if (Diseases.HospitalCanTreat(p.Case) && which(p)) _marked.Add(p.Case);
                RefreshPatients();
            });
        }

        private void SelectMethod(int index)
        {
            Guard("method", () =>
            {
                _method = index;
                for (int i = 0; i < Methods.Count; i++) Methods[i].IsSelected = i == index;
                RefreshPatients();
            });
        }

        private void HealWounds(int kind)
        {
            Guard("wounds", () =>
            {
                if (Diseases.HospitalHealWounds(kind, _st)) Status = "";
                Rebuild();
            });
        }

        private void BuyPrevention(HospitalPrevention p)
        {
            Guard("prevention", () =>
            {
                if (Diseases.HospitalBuyPrevention(p, _st)) Status = "";
                Rebuild();
            });
        }

        private static void Guard(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Error("Hospital window: " + what, ex); }
        }
    }
}
