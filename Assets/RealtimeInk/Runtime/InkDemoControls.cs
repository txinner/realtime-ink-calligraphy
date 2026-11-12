using UnityEngine;
using UnityEngine.InputSystem;

namespace RealtimeInk
{
    public sealed class InkDemoControls : MonoBehaviour
    {
        public RealtimeInkCanvas ink;
        void Update()
        {
            if (!ink || Keyboard.current == null) return;
            var keyboard = Keyboard.current;
            if (keyboard.ctrlKey.isPressed || keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed)
            {
                if (keyboard.zKey.wasPressedThisFrame)
                {
                    if (keyboard.shiftKey.isPressed) ink.RedoStroke(); else ink.UndoStroke();
                }
                if (keyboard.yKey.wasPressedThisFrame) ink.RedoStroke();
                if (keyboard.sKey.wasPressedThisFrame) ink.SaveTransparentPng();
                return;
            }
            if (Keyboard.current.cKey.wasPressedThisFrame) ink.ClearCanvas();
            if (Keyboard.current.rKey.wasPressedThisFrame) ink.RefillInk();
            if (Keyboard.current.dKey.wasPressedThisFrame)
                ink.appearance.debugView = (InkDebugView)(((int)ink.appearance.debugView + 1) % 5);
            if (Keyboard.current.digit1Key.wasPressedThisFrame) ink.ApplyStyle(InkStyle.Regular);
            if (Keyboard.current.digit2Key.wasPressedThisFrame) ink.ApplyStyle(InkStyle.Running);
            if (Keyboard.current.digit3Key.wasPressedThisFrame) ink.ApplyStyle(InkStyle.Cursive);
            if (Keyboard.current.digit4Key.wasPressedThisFrame) ink.ApplyStyle(InkStyle.Seal);
            if (Keyboard.current.digit5Key.wasPressedThisFrame) ink.ApplyStyle(InkStyle.Clerical);
            if (Keyboard.current.digit6Key.wasPressedThisFrame) ink.ApplyStyle(InkStyle.Stele);
            bool changed = false;
            if (Keyboard.current.hKey.wasPressedThisFrame) { ink.techniques.silkwormSwallowtail = !ink.techniques.silkwormSwallowtail; changed = true; }
            if (Keyboard.current.uKey.wasPressedThisFrame) { ink.techniques.uniformLine = !ink.techniques.uniformLine; changed = true; }
            if (Keyboard.current.bKey.wasPressedThisFrame) { ink.techniques.stoneEdges = !ink.techniques.stoneEdges; changed = true; }
            if (changed) GetComponent<InkDemoPanel>()?.Refresh();
        }
    }
}
