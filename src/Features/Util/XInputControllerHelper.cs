using System.Runtime.InteropServices;

namespace UnityVRMod.Features.Util
{
    internal readonly struct XInputControllerState
    {
        public readonly Vector2 LeftStick;
        public readonly Vector2 RightStick;
        public readonly float LeftTrigger;
        public readonly float RightTrigger;
        public readonly bool BackButtonPressed;
        public readonly bool StartButtonPressed;
        public readonly bool RightStickButtonPressed;
        public readonly bool LeftStickButtonPressed;
        public readonly bool LeftShoulderPressed;
        public readonly bool RightShoulderPressed;
        public readonly bool XButtonPressed;
        public readonly bool YButtonPressed;

        public XInputControllerState(Vector2 leftStick, Vector2 rightStick, float leftTrigger, float rightTrigger, bool backButtonPressed, bool startButtonPressed, bool rightStickButtonPressed, bool leftStickButtonPressed, bool leftShoulderPressed, bool rightShoulderPressed, bool xButtonPressed, bool yButtonPressed)
        {
            LeftStick = leftStick;
            RightStick = rightStick;
            LeftTrigger = leftTrigger;
            RightTrigger = rightTrigger;
            BackButtonPressed = backButtonPressed;
            StartButtonPressed = startButtonPressed;
            RightStickButtonPressed = rightStickButtonPressed;
            LeftStickButtonPressed = leftStickButtonPressed;
            LeftShoulderPressed = leftShoulderPressed;
            RightShoulderPressed = rightShoulderPressed;
            XButtonPressed = xButtonPressed;
            YButtonPressed = yButtonPressed;
        }
    }

    internal static class XInputControllerHelper
    {
        private const string NativeHelperDll = "UnityGraphicsHelper";
        private const uint ERROR_SUCCESS = 0;
        private const ushort XINPUT_GAMEPAD_BACK = 0x0020;
        private const ushort XINPUT_GAMEPAD_START = 0x0010;
        private const ushort XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
        private const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
        private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
        private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        private const ushort XINPUT_GAMEPAD_X = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y = 0x8000;
        private const short XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE = 7849;
        private const short XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE = 8689;
        private const byte XINPUT_GAMEPAD_TRIGGER_THRESHOLD = 30;

        private static bool? _useXInput14;
        private static bool? _nativeHelperAvailable;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
        private static extern uint XInputGetState14(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
        private static extern uint XInputGetState910(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport(NativeHelperDll, EntryPoint = "GetFilteredXInputState", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint GetFilteredXInputState(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport(NativeHelperDll, EntryPoint = "SetXInputFilteringEnabled", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SetXInputFilteringEnabled(int enabled);

        public static void SetGameXInputFilterEnabled(bool enabled)
        {
            if (_nativeHelperAvailable.HasValue && !_nativeHelperAvailable.Value)
            {
                return;
            }

            try
            {
                SetXInputFilteringEnabled(enabled ? 1 : 0);
                _nativeHelperAvailable = true;
            }
            catch (DllNotFoundException)
            {
                _nativeHelperAvailable = false;
            }
            catch (EntryPointNotFoundException)
            {
                _nativeHelperAvailable = false;
            }
            catch (BadImageFormatException)
            {
                _nativeHelperAvailable = false;
            }
        }

        public static bool TryGetPrimaryControllerState(out XInputControllerState state)
        {
            if (!_nativeHelperAvailable.HasValue || _nativeHelperAvailable.Value)
            {
                if (TryGetStateFromNativeHelper(out state))
                {
                    return true;
                }
            }

            if (_useXInput14.HasValue)
            {
                return _useXInput14.Value
                    ? TryGetState14(out state)
                    : TryGetState910(out state);
            }

            try
            {
                if (TryGetState14(out state))
                {
                    _useXInput14 = true;
                    return true;
                }

                _useXInput14 = true;
                return false;
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            try
            {
                if (TryGetState910(out state))
                {
                    _useXInput14 = false;
                    return true;
                }

                _useXInput14 = false;
                return false;
            }
            catch
            {
                state = default;
                _useXInput14 = false;
                return false;
            }
        }

        private static bool TryGetStateFromNativeHelper(out XInputControllerState state)
        {
            try
            {
                uint result = GetFilteredXInputState(0, out XINPUT_STATE rawState);
                if (result != ERROR_SUCCESS)
                {
                    state = default;
                    _nativeHelperAvailable = true;
                    return false;
                }

                state = Convert(rawState.Gamepad);
                _nativeHelperAvailable = true;
                return true;
            }
            catch (DllNotFoundException)
            {
                _nativeHelperAvailable = false;
                state = default;
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                _nativeHelperAvailable = false;
                state = default;
                return false;
            }
            catch (BadImageFormatException)
            {
                _nativeHelperAvailable = false;
                state = default;
                return false;
            }
        }

        private static bool TryGetState14(out XInputControllerState state)
        {
            uint result = XInputGetState14(0, out XINPUT_STATE rawState);
            if (result != ERROR_SUCCESS)
            {
                state = default;
                return false;
            }

            state = Convert(rawState.Gamepad);
            return true;
        }

        private static bool TryGetState910(out XInputControllerState state)
        {
            uint result = XInputGetState910(0, out XINPUT_STATE rawState);
            if (result != ERROR_SUCCESS)
            {
                state = default;
                return false;
            }

            state = Convert(rawState.Gamepad);
            return true;
        }

        private static XInputControllerState Convert(XINPUT_GAMEPAD gamepad)
        {
            return new XInputControllerState(
                new Vector2(NormalizeThumbAxis(gamepad.sThumbLX, XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE), NormalizeThumbAxis(gamepad.sThumbLY, XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE)),
                new Vector2(NormalizeThumbAxis(gamepad.sThumbRX, XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE), NormalizeThumbAxis(gamepad.sThumbRY, XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE)),
                NormalizeTrigger(gamepad.bLeftTrigger),
                NormalizeTrigger(gamepad.bRightTrigger),
                (gamepad.wButtons & XINPUT_GAMEPAD_BACK) != 0,
                (gamepad.wButtons & XINPUT_GAMEPAD_START) != 0,
                (gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0,
                (gamepad.wButtons & XINPUT_GAMEPAD_LEFT_THUMB) != 0,
                (gamepad.wButtons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0,
                (gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0,
                (gamepad.wButtons & XINPUT_GAMEPAD_X) != 0,
                (gamepad.wButtons & XINPUT_GAMEPAD_Y) != 0);
        }

        private static float NormalizeThumbAxis(short value, short deadzone)
        {
            // Avoid overflow on short.MinValue (-32768), which can occur on full analog deflection.
            int abs = value == short.MinValue ? short.MaxValue + 1 : Math.Abs(value);
            if (abs <= deadzone)
            {
                return 0f;
            }

            float normalized = (abs - deadzone) / (32767f - deadzone);
            return Math.Sign(value) * Mathf.Clamp01(normalized);
        }

        private static float NormalizeTrigger(byte value)
        {
            if (value <= XINPUT_GAMEPAD_TRIGGER_THRESHOLD)
            {
                return 0f;
            }

            return Mathf.Clamp01((value - XINPUT_GAMEPAD_TRIGGER_THRESHOLD) / (255f - XINPUT_GAMEPAD_TRIGGER_THRESHOLD));
        }
    }
}