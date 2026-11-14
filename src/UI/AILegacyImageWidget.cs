using System;
using System.IO;
using AILegacy.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.TwoDimension;

namespace AILegacy.UI
{
    /// <summary>
    /// Draws a PNG from disk. Used from GUI/Prefabs/AILegacyBook.xml as &lt;AILegacyImageWidget ImagePath="@DetailImage" /&gt;.
    /// The game's TextureWidget only takes textures from a provider registered at start-up, so this one loads the file
    /// and draws it itself, the same way TextureWidget does.
    /// </summary>
    public class AILegacyImageWidget : Widget
    {
        private string _imagePath = "";
        private Texture _texture;
        private TaleWorlds.Engine.Texture _engine;
        private bool _failed;

        public AILegacyImageWidget(UIContext context) : base(context) { }

        [Editor(false)]
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                value = value ?? "";
                if (value == _imagePath) return;
                _imagePath = value;
                Drop(); _failed = false;
                OnPropertyChanged(value, nameof(ImagePath));
            }
        }

        /// <summary>Lets go of the loaded picture so the engine can free it.</summary>
        private void Drop()
        {
            _texture = null;
            var e = _engine; _engine = null;
            try { e?.Release(); } catch { }
        }

        protected override void OnDisconnectedFromRoot()
        {
            base.OnDisconnectedFromRoot();
            Drop();
        }

        private void Load()
        {
            try
            {
                if (_imagePath.Length == 0 || !File.Exists(_imagePath)) { _failed = true; return; }
                var engine = TaleWorlds.Engine.Texture.LoadTextureFromPath(Path.GetFileName(_imagePath), Path.GetDirectoryName(_imagePath));
                if (engine == null) { _failed = true; return; }
                engine.PreloadTexture(false);
                _engine = engine;
                _texture = new Texture(new EngineTexture(engine));
            }
            catch (Exception ex) { _failed = true; Log.Error("Imagen: cargar " + _imagePath, ex); }
        }

        protected override void OnRender(TwoDimensionContext twoDimensionContext, TwoDimensionDrawContext drawContext)
        {
            if (_texture == null && !_failed && _imagePath.Length > 0) Load();
            if (_texture == null || !_texture.IsValid) return;
            try
            {
                SimpleMaterial material = drawContext.CreateSimpleMaterial();
                material.OverlayEnabled = false;
                material.CircularMaskingEnabled = false;
                material.Texture = _texture;
                material.NinePatchParameters = SpriteNinePatchParameters.Empty;
                material.AlphaFactor = Context.ContextAlpha;
                material.ColorFactor = 1f;
                material.HueFactor = 0f;
                material.SaturationFactor = 0f;
                material.ValueFactor = 0f;
                material.Color = Color.White;
                ImageDrawObject drawObject = ImageDrawObject.Create(in AreaRect, in Vec2.Zero, in Vec2.One);
                drawObject.Scale = _scaleToUse;
                drawContext.Draw(material, in drawObject);
            }
            catch (Exception ex) { _failed = true; Drop(); Log.Error("Imagen: dibujar", ex); }
        }
    }
}
