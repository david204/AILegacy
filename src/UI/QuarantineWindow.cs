using System;
using AILegacy.Core;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace AILegacy.UI
{
    /// <summary>
    /// "Manage quarantine" from the menu of a town or castle the player owns or, as ruler, of a vassal's: the diseases of
    /// the place, the days left and what a quarantine does, with one button to set it (until lifted) or lift it.
    /// Prefab: Module\GUI\Prefabs\AILegacyQuarantine.xml. If the window cannot open, the same as a plain inquiry.
    /// </summary>
    internal static class QuarantineWindow
    {
        public static bool Open()
        {
            var st = Settlement.CurrentSettlement;
            if (!Diseases.CanManageQuarantine(st)) return false;
            var vm = new QuarantineVM(st);
            GauntletWindow w = null;
            vm.OnClose = () => w?.Close();
            w = GauntletWindow.Open("AILegacyQuarantine", vm, () => w?.Close());
            return w != null;
        }

        /// <summary>The fallback: the same facts and the same choice in the game's own inquiry box.</summary>
        public static void Ask()
        {
            try
            {
                var st = Settlement.CurrentSettlement;
                if (!Diseases.CanManageQuarantine(st)) return;
                bool closed = Diseases.IsQuarantined(st);
                string body = HospitalWindow.Clean(Diseases.QuarantineStatus(st)) + "\n\n" + HospitalWindow.Clean(QuarantineVM.ActionHintFor(st, closed));
                InformationManager.ShowInquiry(new InquiryData(L.T("AIL_e7b1d707", "Gestión de la cuarentena"), body, true, true,
                    QuarantineVM.ActionTextFor(st, closed), L.T("AIL_e5c9b3a4", "Cancelar"),
                    () => QuarantineVM.Toggle(st), null));
            }
            catch (Exception ex) { Log.Error("[quarantine] inquiry", ex); }
        }
    }

    public class QuarantineVM : HospitalVMBase
    {
        private readonly Settlement _st;
        public Action OnClose;
        private string _title = "", _body = "", _actionText = "", _actionHint = "";

        internal QuarantineVM(Settlement st)
        {
            _st = st;
            Rebuild();
        }

        [DataSourceProperty] public string CloseText => L.T("AIL_e8438e2a", "Cerrar");
        [DataSourceProperty] public string Title { get => _title; set => SetS(ref _title, value, nameof(Title)); }
        [DataSourceProperty] public string Body { get => _body; set => SetS(ref _body, value, nameof(Body)); }
        [DataSourceProperty] public string ActionText { get => _actionText; set => SetS(ref _actionText, value, nameof(ActionText)); }
        [DataSourceProperty] public string ActionHint { get => _actionHint; set => SetS(ref _actionHint, value, nameof(ActionHint)); }

        public void ExecuteClose() => OnClose?.Invoke();

        public void ExecuteAction()
        {
            try
            {
                Toggle(_st);
                OnClose?.Invoke();
            }
            catch (Exception ex) { Log.Error("Quarantine window: action", ex); }
        }

        internal static string ActionTextFor(Settlement st, bool closed) =>
            closed ? L.T("AIL_e7b1d71d", "Levantar la cuarentena")
                   : st.IsTown ? L.T("AIL_e7b1d71f", "Decretar la cuarentena en la ciudad") : L.T("AIL_e7b1d720", "Decretar la cuarentena en el castillo");

        internal static string ActionHintFor(Settlement st, bool closed) =>
            closed ? L.T("AIL_e7b1d71e", "Abre el asentamiento a las visitas.")
                   : Diseases.At(st) != null ? L.T("AIL_e7b1d721", "Cierra el asentamiento hasta que levantes la cuarentena.")
                   : L.T("AIL_e7b1d724", "No hay ninguna enfermedad activa: la cuarentena traerá duras penalizaciones económicas.");

        /// <summary>Sets the quarantine if there is none, lifts it if there is; then the settlement menu is drawn again.</summary>
        internal static void Toggle(Settlement st)
        {
            if (!Diseases.CanManageQuarantine(st)) return;
            Diseases.SetPlayerQuarantine(st, !Diseases.IsQuarantined(st));
            try { Campaign.Current?.CurrentMenuContext?.Refresh(); } catch { }
        }

        private void Rebuild()
        {
            try
            {
                bool closed = Diseases.IsQuarantined(_st);
                Title = L.T("AIL_e7b1d707", "Gestión de la cuarentena");
                Body = HospitalWindow.Clean(Diseases.QuarantineStatus(_st));
                ActionText = HospitalWindow.Clean(ActionTextFor(_st, closed));
                ActionHint = HospitalWindow.Clean(ActionHintFor(_st, closed));
            }
            catch (Exception ex) { Log.Error("Quarantine window: rebuild", ex); }
        }
    }
}
