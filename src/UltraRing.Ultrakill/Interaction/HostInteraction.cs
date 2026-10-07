using UltraRing.Link;
using UltraRing.Ultrakill.Terrain;
using UnityEngine;
using UnityEngine.UI;

namespace UltraRing.Ultrakill.Interaction
{
    /// <summary>
    /// The host's own interactions (doors, levers, items, Sites of Grace): shows the action the host offers on
    /// ULTRAKILL's HUD and performs it on the interact key (host-contract.md section 7).
    /// </summary>
    internal sealed class HostInteraction
    {
        private const long AckTimeoutMs = 2000;
        private const long PromptPollMs = 150;
        private const long FlashMs = 2000;
        /// <summary>A door or lever needs a moment to move before its collision is worth resampling.</summary>
        private const long ResampleDelayMs = 900;

        private bool _pending;
        private uint _pendingReq;
        private long _pendingAtMs;
        private long _nextPromptPollMs;
        private string _prompt = "";
        private string _flash;
        private long _flashUntilMs;
        private long _resampleAtMs = long.MinValue;
        private Text _label;
        private string _shown;
        private int _performed;

        public string Status => $"prompt '{_prompt}', {_performed} actions{(_pending ? ", waiting for the host" : "")}";

        public void Tick(GuestLink link, bool driving, TerrainManager terrain, CoordMap map, Vector3 feetUk)
        {
            long now = link.NowMs;
            if (now >= _nextPromptPollMs)
            {
                _nextPromptPollMs = now + PromptPollMs;
                _prompt = driving ? link.HostPrompt : "";
            }

            if (driving && !_pending && Input.GetKeyDown(BridgeConfig.InteractKey.Value))
            {
                _pendingReq = link.RequestAction();
                _pending = _pendingReq != 0;
                _pendingAtMs = now;
            }
            if (_pending)
            {
                if (link.ActionAck == _pendingReq)
                {
                    _pending = false;
                    HandleResult(link.ActionResult, now);
                }
                else if (now - _pendingAtMs > AckTimeoutMs)
                {
                    _pending = false;
                    Flash("The host did not answer", now);
                }
            }

            if (_resampleAtMs != long.MinValue && now >= _resampleAtMs && map != null)
            {
                _resampleAtMs = long.MinValue;
                terrain.Invalidate(feetUk, map.ToUkLength(8f));
            }

            UpdateLabel(now);
        }

        private void HandleResult(int result, long now)
        {
            switch (result)
            {
                case 1:
                    _performed++;
                    _resampleAtMs = now + ResampleDelayMs;
                    break;
                case 0:
                    Flash("Nothing to interact with here", now);
                    break;
                case -2:
                    Flash($"Ladders need Elden Ring's controls ({BridgeConfig.SwitchKey.Value})", now);
                    break;
                default:
                    Flash("Not supported here", now);
                    break;
            }
        }

        private void Flash(string text, long now)
        {
            _flash = text;
            _flashUntilMs = now + FlashMs;
        }

        // ---- HUD label on ULTRAKILL's overlay canvas (captured into the host's GUI layer) ----------

        private void UpdateLabel(long now)
        {
            string text = now < _flashUntilMs ? _flash
                : string.IsNullOrEmpty(_prompt) ? "" : $"[{BridgeConfig.InteractKey.Value}]  {_prompt}";
            if (text == _shown && _label != null) return;
            if (!EnsureLabel()) return;
            _shown = text;
            _label.text = text;
            _label.enabled = text.Length > 0;
        }

        private bool EnsureLabel()
        {
            if (_label != null) return true;
            if (!MonoSingleton<CanvasController>.TryGetInstance(out var canvas) || canvas == null) return false;
            var go = new GameObject("UltraRing Host Prompt", typeof(RectTransform));
            go.layer = canvas.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas.transform, false);
            rt.anchorMin = new Vector2(0.5f, 0.2f);
            rt.anchorMax = new Vector2(0.5f, 0.2f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1200f, 60f);
            _label = go.AddComponent<Text>();
            _label.font = LoadFont();
            _label.fontSize = 30;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = Color.white;
            _label.raycastTarget = false;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(2f, -2f);
            _shown = null;
            return true;
        }

        private static Font LoadFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null) try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            return f;
        }
    }
}
