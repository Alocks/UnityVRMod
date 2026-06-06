namespace UnityVRMod.Features.VrVisualization
{
    public struct VrCameraRig
    {
        public GameObject LeftEye;
        public GameObject RightEye;
    }

    internal interface IVrCameraSetup
    {
        bool IsVrAvailable { get; }
        bool InitializeVr(string applicationKey);
        void TeardownVr();
        void SetupCameraRig(Camera mainCamera, bool useFullCameraRotation = false);
        void TeardownCameraRig();
        VrCameraRig GetVrCameraGameObjects();
        void UpdatePoses();
        void MoveRig(Vector3 localDelta);
        void MoveRigWorld(Vector3 worldDelta);
        void RotateRig(float yawDegrees);
        void TiltRig(float pitchDegrees);
        void RollRig(float rollDegrees);
        void RotateRigWorldDelta(Quaternion worldDeltaRotation);
        void AlignRigToCameraRotation(Camera mainCamera, bool useFullCameraRotation = false);
        void ResetRigToCenter();
        void SetCurrentPositionAsDefault();

        // --- METHODS FOR LIVE RELOADING ---
        void SetWorldScale(float newWorldScale, Camera mainCamera);
        void SetCameraNearClip(float newNearClip);
        void SetUserEyeHeightOffset(float newEyeHeightOffset);
    }
}