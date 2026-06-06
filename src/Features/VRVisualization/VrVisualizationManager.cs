using UnityVRMod.Config;
using UnityVRMod.Core;
using UnityVRMod.Features.Util;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

#if CPP
using Il2CppInterop.Runtime;
#endif

namespace UnityVRMod.Features.VrVisualization
{
    internal class VrVisualizationManager
    {
        private IVrCameraSetup _cameraSetup;
        private bool _managerInitialized = false;
        private GameObject _currentlyTrackedOriginalCameraGO = null;
        private int _currentlyTrackedOriginalCameraInstanceId = 0;

        private bool _isUserSafeModeActive = true;
        private float _autoSafeModeEndTime = -1f;
        private bool _lastControllerBackButtonPressed = false;
        private bool _lastControllerStartButtonPressed = false;
        private bool _lastControllerLeftStickButtonPressed = false;
        private bool _lastControllerLeftShoulderPressed = false;
        private bool _lastControllerRightShoulderPressed = false;
        private bool _lastControllerXButtonPressed = false;
        private bool _lastControllerYButtonPressed = false;
        private bool _inputSystemGamepadsSuppressed = false;
        private float _nextWorldScaleRepeatTime = -1f;
        private int _forcedCameraInstanceId = 0;
        private float _lastRigSetupTime = -1000f;
        private bool _useFullRotationOnNextRigSetup = false;
        private int _followSourceCameraInstanceId = 0;
        private Vector3 _lastFollowSourceCameraPosition = Vector3.zero;
        private Quaternion _lastFollowSourceCameraRotation = Quaternion.identity;
        private bool _followSourceCameraFullRotation = false;

        private const float ControllerWorldScaleStep = 0.02f;
        private const float ControllerWorldScaleRepeatInterval = 0.10f;
        private const float MinSourceCameraPositionDelta = 0.00005f;
        private const float MinSourceCameraYawDelta = 0.01f;

        private UnityAction<Scene, Scene> _sceneChangedActionDelegate;

        private bool _hasVrBeenAttemptedByUser = false;
        internal bool IsVrReady => _hasVrBeenAttemptedByUser && _cameraSetup != null && _cameraSetup.IsVrAvailable;

        internal Camera VrCameraForUIParenting
        {
            get
            {
                if (!IsVrReady) return null;
                VrCameraRig rig = _cameraSetup.GetVrCameraGameObjects();
                if (rig.LeftEye != null) return rig.LeftEye.GetComponent<Camera>();
                if (rig.RightEye != null) return rig.RightEye.GetComponent<Camera>();
                return null;
            }
        }

        internal void Initialize()
        {
            if (_managerInitialized) return;
            _managerInitialized = true;

            if (!ConfigManager.EnableVrInjection.Value)
            {
                VRModCore.Log("VR Visualization feature is disabled by config.");
                return;
            }

            _isUserSafeModeActive = ConfigManager.SafeModeStartsActive.Value;
            VRModCore.Log($"Initial user safe mode active: {_isUserSafeModeActive}. VR init will be delayed until user first deactivates Safe Mode.");

            if (ConfigManager.EnableAutomaticSafeMode.Value)
            {
                VRModCore.LogRuntimeDebug("Automatic safe mode enabled, subscribing to scene changes.");
#if CPP
                Action<Scene, Scene> csAction = OnActiveSceneChanged;
                _sceneChangedActionDelegate = DelegateSupport.ConvertDelegate<UnityAction<Scene, Scene>>(csAction);
                if (_sceneChangedActionDelegate != null) SceneManager.activeSceneChanged += _sceneChangedActionDelegate;
                else VRModCore.LogError("(IL2CPP) Failed to convert scene change delegate.");
#else
                _sceneChangedActionDelegate = OnActiveSceneChanged;
                SceneManager.activeSceneChanged += _sceneChangedActionDelegate;
#endif
            }
            VRModCore.Log("VrVisualizationManager initialized.");
        }

        private bool EnsureAndInitializeVrSubsystem()
        {
            if (_cameraSetup != null && _cameraSetup.IsVrAvailable)
            {
                VRModCore.LogRuntimeDebug("VR subsystem already initialized and available.");
                return true;
            }

            VRModCore.LogRuntimeDebug("Attempting to initialize VR subsystem...");

            string cameraSetupTypeNameFull;
#if OPENVR_BUILD
            cameraSetupTypeNameFull = "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR";
#elif OPENXR_BUILD
            cameraSetupTypeNameFull = "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenXR";
#else
            VRModCore.LogError("Critical Error! No VR backend build symbol (OPENVR_BUILD or OPENXR_BUILD) defined!");
            return false;
#endif

            try
            {
                Type setupType = Type.GetType(cameraSetupTypeNameFull);
                if (setupType == null || !typeof(IVrCameraSetup).IsAssignableFrom(setupType))
                {
                    VRModCore.LogError($"Type '{cameraSetupTypeNameFull}' not found or invalid.");
                    return false;
                }
                _cameraSetup = Activator.CreateInstance(setupType) as IVrCameraSetup;
                if (_cameraSetup == null)
                {
                    VRModCore.LogError($"Failed to create instance of '{cameraSetupTypeNameFull}'.");
                    return false;
                }

                VRModCore.Log($"Loaded {_cameraSetup.GetType().Name}. Initializing VR subsystem...");
                if (!_cameraSetup.InitializeVr(ConfigManager.VrApplicationKey.Value))
                {
                    VRModCore.LogWarning("VR subsystem initialization failed.");
                    _cameraSetup = null;
                    return false;
                }

                VRModCore.LogRuntimeDebug("VR subsystem initialization successful.");
                _hasVrBeenAttemptedByUser = true;
                                
                return true;
            }
            catch (Exception ex)
            {
                VRModCore.LogError("Exception during VR subsystem instantiation or initialization:", ex);
                _cameraSetup = null;
                return false;
            }
        }

        private void OnActiveSceneChanged(Scene current, Scene next)
        {
            VRModCore.LogRuntimeDebug($"Scene changed from '{current.name}' to '{next.name}'.");
            CameraFinder.InvalidateCache(); // Invalidate cache on scene change.
            if (!IsVrReady) return;

            if (ConfigManager.EnableAutomaticSafeMode.Value)
            {
                ActivateAutomaticSafeMode($"Scene changed to '{next.name}'");
            }
        }

        private void ActivateAutomaticSafeMode(string reason)
        {
            if (!IsVrReady || !ConfigManager.EnableAutomaticSafeMode.Value) return;

            float duration = ConfigManager.AutomaticSafeModeDurationSecs.Value;
            _autoSafeModeEndTime = Time.time + duration;
            VRModCore.Log($"Automatic Safe Mode ENGAGED for {duration:F1}s. Reason: {reason}.");
        }

        private void UpdateControllerCameraControl(bool rigIsSetUp)
        {
            if (!rigIsSetUp || _cameraSetup == null || !_cameraSetup.IsVrAvailable) return;
            if (!ConfigManager.EnableControllerCameraControl.Value) return;
            if (_isUserSafeModeActive || Time.time < _autoSafeModeEndTime) return;

            if (!UnityVRMod.Features.Util.XInputControllerHelper.TryGetPrimaryControllerState(out var controllerState))
            {
                _lastControllerBackButtonPressed = false;
                _lastControllerStartButtonPressed = false;
                _lastControllerLeftStickButtonPressed = false;
                _lastControllerLeftShoulderPressed = false;
                _lastControllerRightShoulderPressed = false;
                _lastControllerXButtonPressed = false;
                _lastControllerYButtonPressed = false;
                _nextWorldScaleRepeatTime = -1f;
                return;
            }

            float deadzone = Mathf.Clamp01(ConfigManager.ControllerCameraDeadzone.Value);

            Vector2 moveStick = controllerState.LeftStick;
            Vector2 turnStick = controllerState.RightStick;
            float verticalAxis = controllerState.RightTrigger - controllerState.LeftTrigger;

            if (controllerState.StartButtonPressed && !_lastControllerStartButtonPressed)
            {
                _cameraSetup.SetCurrentPositionAsDefault();
                VRModCore.LogRuntimeDebug("Controller start button pressed. VR rig current pose saved as new default.");
            }
            _lastControllerStartButtonPressed = controllerState.StartButtonPressed;

            if (controllerState.LeftStickButtonPressed && !_lastControllerLeftStickButtonPressed)
            {
                float defaultScale = ConfigManager.VrWorldScale.DefaultValue is float value ? value : 1.0f;
                ConfigManager.VrWorldScale.Value = Mathf.Clamp(defaultScale, 0.1f, 10f);
                VRModCore.LogRuntimeDebug($"Controller L3 pressed. World scale reset to default: {ConfigManager.VrWorldScale.Value:F2}");
            }
            _lastControllerLeftStickButtonPressed = controllerState.LeftStickButtonPressed;

            if (controllerState.XButtonPressed && !_lastControllerXButtonPressed)
            {
                CycleInjectedCamera(true);
            }
            _lastControllerXButtonPressed = controllerState.XButtonPressed;

            if (controllerState.YButtonPressed && !_lastControllerYButtonPressed)
            {
                CycleInjectedCamera(false);
            }
            _lastControllerYButtonPressed = controllerState.YButtonPressed;

            bool decreaseHeld = controllerState.LeftShoulderPressed;
            bool increaseHeld = controllerState.RightShoulderPressed;
            int worldScaleDirection = (decreaseHeld == increaseHeld) ? 0 : (decreaseHeld ? -1 : 1);

            if (worldScaleDirection == 0)
            {
                _nextWorldScaleRepeatTime = -1f;
            }
            else
            {
                bool isInitialPress = (worldScaleDirection < 0 && !_lastControllerLeftShoulderPressed)
                    || (worldScaleDirection > 0 && !_lastControllerRightShoulderPressed);

                if (isInitialPress || Time.unscaledTime >= _nextWorldScaleRepeatTime)
                {
                    float delta = worldScaleDirection * ControllerWorldScaleStep;
                    float newScale = Mathf.Clamp(ConfigManager.VrWorldScale.Value + delta, 0.1f, 10f);
                    ConfigManager.VrWorldScale.Value = newScale;
                    _nextWorldScaleRepeatTime = Time.unscaledTime + ControllerWorldScaleRepeatInterval;
                }
            }

            _lastControllerLeftShoulderPressed = decreaseHeld;
            _lastControllerRightShoulderPressed = controllerState.RightShoulderPressed;

            bool resetPressed = controllerState.BackButtonPressed || controllerState.RightStickButtonPressed;
            if (resetPressed && !_lastControllerBackButtonPressed)
            {
                _cameraSetup.ResetRigToCenter();
                VRModCore.LogRuntimeDebug("Controller reset button pressed. VR rig recentered.");
            }
            _lastControllerBackButtonPressed = resetPressed;

            moveStick = ApplyDeadzone(moveStick, deadzone);
            turnStick = ApplyDeadzone(turnStick, deadzone);
            verticalAxis = ApplyDeadzone(verticalAxis, deadzone);

            if (moveStick == Vector2.zero && turnStick == Vector2.zero && Mathf.Approximately(verticalAxis, 0f))
            {
                return;
            }

            float deltaTime = Time.unscaledDeltaTime;
            float moveSpeed = ConfigManager.ControllerCameraMoveSpeed.Value;
            float turnSpeed = ConfigManager.ControllerCameraTurnSpeed.Value;
            float verticalSpeed = ConfigManager.ControllerCameraVerticalSpeed.Value;

            float yawDelta = turnStick.x * turnSpeed * deltaTime;
            if (!Mathf.Approximately(yawDelta, 0f))
            {
                _cameraSetup.RotateRig(yawDelta);
            }

            float pitchDelta = -turnStick.y * turnSpeed * deltaTime;
            if (!Mathf.Approximately(pitchDelta, 0f))
            {
                _cameraSetup.TiltRig(pitchDelta);
            }

            Vector3 localDelta = new Vector3(
                moveStick.x * moveSpeed * deltaTime,
                verticalAxis * verticalSpeed * deltaTime,
                moveStick.y * moveSpeed * deltaTime);

            if (localDelta != Vector3.zero)
            {
                _cameraSetup.MoveRig(localDelta);
            }
        }

        private static Vector2 ApplyDeadzone(Vector2 value, float deadzone)
        {
            float x = ApplyDeadzone(value.x, deadzone);
            float y = ApplyDeadzone(value.y, deadzone);
            return new Vector2(x, y);
        }

        private static float ApplyDeadzone(float value, float deadzone)
        {
            float abs = Mathf.Abs(value);
            if (abs <= deadzone) return 0f;

            float normalized = (abs - deadzone) / Mathf.Max(0.0001f, 1f - deadzone);
            return Mathf.Sign(value) * Mathf.Clamp01(normalized);
        }

        private void ResetSourceCameraFollowState()
        {
            _followSourceCameraInstanceId = 0;
            _lastFollowSourceCameraPosition = Vector3.zero;
            _lastFollowSourceCameraRotation = Quaternion.identity;
        }

        private void PrimeSourceCameraFollow(Camera sourceCamera)
        {
            if (sourceCamera == null)
            {
                ResetSourceCameraFollowState();
                return;
            }

            _followSourceCameraInstanceId = sourceCamera.GetInstanceID();
            _lastFollowSourceCameraPosition = sourceCamera.transform.position;
            _lastFollowSourceCameraRotation = sourceCamera.transform.rotation;
        }

        private void UpdateSourceCameraFollow(Camera sourceCamera)
        {
            if (sourceCamera == null || _cameraSetup == null || !_cameraSetup.IsVrAvailable) return;

            int sourceCameraId = sourceCamera.GetInstanceID();
            if (_followSourceCameraInstanceId != sourceCameraId)
            {
                PrimeSourceCameraFollow(sourceCamera);
                return;
            }

            Vector3 currentPosition = sourceCamera.transform.position;
            Quaternion currentRotation = sourceCamera.transform.rotation;

            Vector3 worldDelta = currentPosition - _lastFollowSourceCameraPosition;
            if (worldDelta.sqrMagnitude > MinSourceCameraPositionDelta)
            {
                _cameraSetup.MoveRigWorld(worldDelta);
            }

            if (_followSourceCameraFullRotation)
            {
                _cameraSetup.AlignRigToCameraRotation(sourceCamera, true);
            }
            else
            {
                float yawDelta = Mathf.DeltaAngle(_lastFollowSourceCameraRotation.eulerAngles.y, currentRotation.eulerAngles.y);
                if (Mathf.Abs(yawDelta) > MinSourceCameraYawDelta)
                {
                    _cameraSetup.RotateRig(yawDelta);
                }
            }

            _lastFollowSourceCameraPosition = currentPosition;
            _lastFollowSourceCameraRotation = currentRotation;
        }

        private void UpdateGameControllerSuppression(bool shouldSuppress)
        {
            float warmupSecs = Mathf.Max(0f, ConfigManager.NativeControllerSuppressionWarmupSecs.Value);
            bool warmupElapsed = (Time.unscaledTime - _lastRigSetupTime) >= warmupSecs;
            bool nativeSuppressionAllowed = shouldSuppress
                && ConfigManager.EnableNativeControllerSuppression.Value
                && warmupElapsed;

            XInputControllerHelper.SetGameXInputFilterEnabled(nativeSuppressionAllowed);

            if (shouldSuppress)
            {
                // Keep camera control on direct XInput while neutralizing Unity input consumed by the game.
                UniverseLib.Input.InputManager.ResetInputAxes();

                if (!_inputSystemGamepadsSuppressed)
                {
                    _inputSystemGamepadsSuppressed = TrySetInputSystemGamepadsEnabled(false);
                }
            }
            else if (_inputSystemGamepadsSuppressed)
            {
                TrySetInputSystemGamepadsEnabled(true);
                _inputSystemGamepadsSuppressed = false;
            }
        }

        private static bool TrySetInputSystemGamepadsEnabled(bool enabled)
        {
            try
            {
                Type inputSystemType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
                Type inputDeviceType = Type.GetType("UnityEngine.InputSystem.InputDevice, Unity.InputSystem");
                Type gamepadType = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
                if (inputSystemType == null || inputDeviceType == null || gamepadType == null) return false;

                MethodInfo toggleMethod = inputSystemType.GetMethod(
                    enabled ? "EnableDevice" : "DisableDevice",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    [inputDeviceType],
                    null);
                PropertyInfo allGamepadsProp = gamepadType.GetProperty("all", BindingFlags.Public | BindingFlags.Static);
                if (toggleMethod == null || allGamepadsProp == null) return false;

                object allGamepads = allGamepadsProp.GetValue(null, null);
                if (allGamepads is not System.Collections.IEnumerable enumerable) return false;

                bool toggledAny = false;
                foreach (object gamepad in enumerable)
                {
                    if (gamepad == null) continue;
                    toggleMethod.Invoke(null, [gamepad]);
                    toggledAny = true;
                }

                return toggledAny;
            }
            catch (Exception ex)
            {
                VRModCore.LogRuntimeDebug($"InputSystem gamepad suppression failed: {ex.Message}");
                return false;
            }
        }

        public void ToggleUserSafeMode()
        {
            _isUserSafeModeActive = !_isUserSafeModeActive;
            VRModCore.Log($"User Safe Mode Toggled. Now: {(_isUserSafeModeActive ? "ACTIVE (Rendering OFF)" : "INACTIVE (Rendering ON)")}");

            if (_isUserSafeModeActive)
            {
                if (_cameraSetup != null)
                {
                    var level = ConfigManager.ActiveSafeModeLevel.Value;
                    VRModCore.LogRuntimeDebug($"Entering Safe Mode with level: {level}");

                    if (level == SafeModeLevel.FullVrReinitOnToggle)
                    {
                        VRModCore.Log("Tearing down full VR subsystem for re-initialization.");
                        _cameraSetup.TeardownVr();
                        _cameraSetup = null;
                        _hasVrBeenAttemptedByUser = false;
                        CameraFinder.InvalidateCache();
                    }
                    else if (level == SafeModeLevel.RigReinitOnToggle)
                    {
                        VRModCore.Log("Tearing down VR camera rig for re-initialization.");
                        _cameraSetup.TeardownCameraRig();
                        _currentlyTrackedOriginalCameraGO = null;
                        CameraFinder.InvalidateCache();
                    }
                }
            }
            else
            {
                _autoSafeModeEndTime = -1f;
                if (!_hasVrBeenAttemptedByUser)
                {
                    VRModCore.LogRuntimeDebug("First time user is disabling Safe Mode. Attempting to initialize VR subsystem...");
                    if (!EnsureAndInitializeVrSubsystem())
                    {
                        VRModCore.LogError("Failed to initialize VR subsystem. Re-enabling Safe Mode.");
                        _isUserSafeModeActive = true;
                        _hasVrBeenAttemptedByUser = false;
                    }
                }
            }
        }

        internal void Update()
        {
            if (_cameraSetup == null && _hasVrBeenAttemptedByUser)
            {
                UpdateGameControllerSuppression(false);
                if (!_isUserSafeModeActive)
                {
                    VRModCore.LogRuntimeDebug("VR Subsystem is fully torn down. Re-initializing due to user exiting safe mode.");
                    if (!EnsureAndInitializeVrSubsystem())
                    {
                        VRModCore.LogError("Failed to re-initialize VR subsystem. Re-enabling Safe Mode.");
                        _isUserSafeModeActive = true;
                    }
                }
                return;
            }

            if (!_hasVrBeenAttemptedByUser || _cameraSetup == null || !_cameraSetup.IsVrAvailable)
            {
                UpdateGameControllerSuppression(false);
                return;
            }

            bool autoSafeModeEngaged = Time.time < _autoSafeModeEndTime;
            bool shouldRender = !_isUserSafeModeActive && !autoSafeModeEngaged;

            VrCameraRig vrCameras = _cameraSetup.GetVrCameraGameObjects();
            bool rigIsSetUp = vrCameras.LeftEye != null || vrCameras.RightEye != null;

            Camera mainCam = FindTargetInjectionCamera();

            if (mainCam != null)
            {
                if (rigIsSetUp)
                {
                    if (_useFullRotationOnNextRigSetup
                        || _currentlyTrackedOriginalCameraGO != mainCam.gameObject
                        || _currentlyTrackedOriginalCameraInstanceId != mainCam.GetInstanceID())
                    {
                        bool useFullRotationNow = _useFullRotationOnNextRigSetup;
                        VRModCore.Log($"Game's main camera changed to '{mainCam.name}'. Re-creating VR rig.");
                        _cameraSetup.TeardownCameraRig();
                        CameraFinder.InvalidateCache(); // Invalidate since we are about to re-setup
                        _cameraSetup.SetupCameraRig(mainCam, useFullRotationNow);
                        if (useFullRotationNow)
                        {
                            _cameraSetup.AlignRigToCameraRotation(mainCam, true);
                        }
                        _followSourceCameraFullRotation = useFullRotationNow;
                        PrimeSourceCameraFollow(mainCam);
                        _useFullRotationOnNextRigSetup = false;
                        _lastRigSetupTime = Time.unscaledTime;
                        _currentlyTrackedOriginalCameraGO = mainCam.gameObject;
                        _currentlyTrackedOriginalCameraInstanceId = mainCam.GetInstanceID();
                        if (ConfigManager.EnableAutomaticSafeMode.Value)
                            ActivateAutomaticSafeMode("Main camera changed");
                    }
                }
                else if (shouldRender)
                {
                    bool useFullRotationNow = _useFullRotationOnNextRigSetup;
                    VRModCore.Log($"Found game camera '{mainCam.name}'. Setting up VR rig.");
                    _cameraSetup.SetupCameraRig(mainCam, useFullRotationNow);
                    if (useFullRotationNow)
                    {
                        _cameraSetup.AlignRigToCameraRotation(mainCam, true);
                    }
                    _followSourceCameraFullRotation = useFullRotationNow;
                    PrimeSourceCameraFollow(mainCam);
                    _useFullRotationOnNextRigSetup = false;
                    _lastRigSetupTime = Time.unscaledTime;
                    _currentlyTrackedOriginalCameraGO = mainCam.gameObject;
                    _currentlyTrackedOriginalCameraInstanceId = mainCam.GetInstanceID();
                    if (ConfigManager.EnableAutomaticSafeMode.Value)
                        ActivateAutomaticSafeMode("Initial rig setup");
                }
            }
            else if (rigIsSetUp)
            {
                VRModCore.LogWarning("Game's main camera has become null. Tearing down VR rig to prevent conflicts.");
                _cameraSetup.TeardownCameraRig();
                _currentlyTrackedOriginalCameraGO = null;
                _currentlyTrackedOriginalCameraInstanceId = 0;
                ResetSourceCameraFollowState();
                CameraFinder.InvalidateCache();
            }

            if (shouldRender && rigIsSetUp)
            {
                if (ConfigManager.EnableControllerCameraControl.Value)
                {
                    UpdateGameControllerSuppression(true);
                }
                else
                {
                    UpdateGameControllerSuppression(false);
                }

                UpdateSourceCameraFollow(mainCam);
                UpdateControllerCameraControl(rigIsSetUp);
                _cameraSetup.UpdatePoses();
            }
            else
            {
                UpdateGameControllerSuppression(false);
            }
        }

        private Camera FindTargetInjectionCamera()
        {
            if (_forcedCameraInstanceId != 0)
            {
                var cameras = Camera.allCameras;
                foreach (var cam in cameras)
                {
                    if (cam != null && cam.enabled && cam.GetInstanceID() == _forcedCameraInstanceId)
                    {
                        return cam;
                    }
                }

                VRModCore.LogRuntimeDebug("Previously forced camera is no longer valid. Returning to automatic camera selection.");
                _forcedCameraInstanceId = 0;
            }

            return CameraFinder.FindGameCamera();
        }

        private void CycleInjectedCamera(bool attachFullTransform)
        {
            var candidates = new List<Camera>();
            foreach (var camera in Camera.allCameras)
            {
                if (camera != null && camera.enabled)
                {
                    candidates.Add(camera);
                }
            }

            candidates.Sort((a, b) =>
            {
                int depthCompare = a.depth.CompareTo(b.depth);
                if (depthCompare != 0) return depthCompare;

                int nameCompare = string.Compare(a.name, b.name, StringComparison.Ordinal);
                if (nameCompare != 0) return nameCompare;

                return a.GetInstanceID().CompareTo(b.GetInstanceID());
            });

            if (candidates.Count == 0)
            {
                VRModCore.LogWarning("Camera cycle requested, but no enabled cameras were found.");
                return;
            }

            int currentIndex = -1;
            if (_forcedCameraInstanceId != 0)
            {
                currentIndex = candidates.FindIndex(c => c.GetInstanceID() == _forcedCameraInstanceId);
            }
            else if (_currentlyTrackedOriginalCameraGO != null)
            {
                currentIndex = candidates.FindIndex(c => c.gameObject == _currentlyTrackedOriginalCameraGO);
            }

            int nextIndex = (currentIndex + 1 + candidates.Count) % candidates.Count;
            Camera nextCamera = candidates[nextIndex];
            _forcedCameraInstanceId = nextCamera.GetInstanceID();
            _useFullRotationOnNextRigSetup = attachFullTransform;
            CameraFinder.InvalidateCache();

            string attachMode = attachFullTransform ? "full transform" : "yaw-only";
            VRModCore.Log($"Controller camera cycle: next VR injection target is '{nextCamera.name}' (depth {nextCamera.depth:F1}, attach {attachMode}).");
        }

        internal void Shutdown()
        {
            VRModCore.LogRuntimeDebug("Shutdown called.");
            UpdateGameControllerSuppression(false);
            if (ConfigManager.EnableAutomaticSafeMode.Value && _sceneChangedActionDelegate != null)
            {
                SceneManager.activeSceneChanged -= _sceneChangedActionDelegate;
            }

            if (_cameraSetup != null)
            {
                VRModCore.LogRuntimeDebug($"Shutting down camera setup: {_cameraSetup.GetType().Name}.");
                _cameraSetup.TeardownVr();
            }
            _managerInitialized = false;
        }

        public void LiveUpdateWorldScale(float newScale)
        {
            if (IsVrReady)
            {
                Camera cameraComponent = _currentlyTrackedOriginalCameraGO != null
                    ? _currentlyTrackedOriginalCameraGO.GetComponent<Camera>()
                    : null;
                _cameraSetup.SetWorldScale(newScale, cameraComponent);
            }
        }

        public void LiveUpdateCameraNearClip(float newNearClip)
        {
            if (IsVrReady) _cameraSetup.SetCameraNearClip(newNearClip);
        }

        public void LiveUpdateUserEyeHeightOffset(float newOffset)
        {
            if (IsVrReady) _cameraSetup.SetUserEyeHeightOffset(newOffset);
        }
    }
}