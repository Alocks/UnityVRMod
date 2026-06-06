#include "pch.h"
#include "framework.h"

#include <d3d11.h>
#include <d3d11_1.h>
#include <fstream>
#include <string>
#include <chrono>
#include <iomanip>
#include <sstream>
#include <vector>
#include <unordered_set>
#include <mutex>
#include <cstring>
#include <algorithm>
#include <windows.h>
#include <tlhelp32.h>
#include <xinput.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"

#ifndef E_FAIL
#define E_FAIL 0x80004005
#endif

static ID3D11Device* g_D3D11Device = nullptr;
static ID3D11DeviceContext* s_ImmediateContext = nullptr;

using XInputGetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
using GetProcAddressFn = FARPROC(WINAPI*)(HMODULE, LPCSTR);

struct IatPatchEntry
{
    void** slot;
    void* original;
};

struct InlineHookEntry
{
    void* target;
    void* detour;
    void* trampoline;
    BYTE original[16];
    SIZE_T patchLength;
};

static std::mutex g_XInputHookMutex;
static std::vector<IatPatchEntry> g_IatPatches;
static std::unordered_set<void**> g_PatchedSlots;
static std::vector<InlineHookEntry> g_InlineHooks;
static XInputGetStateFn g_OriginalXInputGetState14 = nullptr;
static XInputGetStateFn g_OriginalXInputGetState910 = nullptr;
static XInputGetStateFn g_OriginalXInputGetState13 = nullptr;
static XInputGetStateFn g_OriginalXInputGetStateEx14 = nullptr;
static XInputGetStateFn g_OriginalXInputGetStateEx910 = nullptr;
static XInputGetStateFn g_OriginalXInputGetStateEx13 = nullptr;
static XInputGetStateFn g_TrampolineXInputGetState14 = nullptr;
static XInputGetStateFn g_TrampolineXInputGetState910 = nullptr;
static XInputGetStateFn g_TrampolineXInputGetState13 = nullptr;
static XInputGetStateFn g_TrampolineXInputGetStateEx14 = nullptr;
static XInputGetStateFn g_TrampolineXInputGetStateEx910 = nullptr;
static XInputGetStateFn g_TrampolineXInputGetStateEx13 = nullptr;
static GetProcAddressFn g_OriginalGetProcAddress = nullptr;
static HMODULE g_SelfModule = nullptr;
static LONG g_XInputFilteringEnabled = 0;

static constexpr WORD XINPUT_GET_STATE_EX_ORDINAL = 100;

static bool IsXInputDllName(const char* name)
{
    if (!name) return false;
    return _stricmp(name, "xinput1_4.dll") == 0
        || _stricmp(name, "xinput9_1_0.dll") == 0
        || _stricmp(name, "xinput1_3.dll") == 0
        || _stricmp(name, "xinput1_2.dll") == 0
        || _stricmp(name, "xinput1_1.dll") == 0;
}

static bool IsKernelDllName(const char* name)
{
    if (!name) return false;
    return _stricmp(name, "kernel32.dll") == 0
        || _stricmp(name, "kernelbase.dll") == 0;
}

static bool IsXInputModuleHandle(HMODULE module)
{
    if (!module) return false;
    char modulePath[MAX_PATH] = {};
    DWORD len = GetModuleFileNameA(module, modulePath, static_cast<DWORD>(sizeof(modulePath)));
    if (len == 0 || len >= sizeof(modulePath)) return false;
    const char* fileName = strrchr(modulePath, '\\');
    fileName = fileName ? (fileName + 1) : modulePath;
    return IsXInputDllName(fileName);
}

static bool IsExecutableProtection(DWORD protect)
{
    DWORD baseProtect = protect & 0xFF;
    return baseProtect == PAGE_EXECUTE
        || baseProtect == PAGE_EXECUTE_READ
        || baseProtect == PAGE_EXECUTE_READWRITE
        || baseProtect == PAGE_EXECUTE_WRITECOPY;
}

static bool IsLikelySafeInlinePatchTarget(void* target, SIZE_T patchLength)
{
    if (!target || patchLength < 12 || patchLength > 16) return false;

    MEMORY_BASIC_INFORMATION mbi = {};
    if (VirtualQuery(target, &mbi, sizeof(mbi)) != sizeof(mbi))
        return false;

    if (mbi.State != MEM_COMMIT || !IsExecutableProtection(mbi.Protect))
        return false;

    const BYTE* code = static_cast<const BYTE*>(target);
    for (SIZE_T i = 0; i < patchLength; ++i)
    {
        // Avoid patching on top of control transfer / terminator bytes.
        if (code[i] == 0xE8 || code[i] == 0xE9 || code[i] == 0xEB || code[i] == 0xC2 || code[i] == 0xC3 || code[i] == 0xCC)
            return false;
    }

    return true;
}

static bool WriteAbsoluteJump(void* address, void* destination, SIZE_T patchLength)
{
    if (!address || !destination || patchLength < 12) return false;

    BYTE patch[16] = {};
    memset(patch, 0x90, sizeof(patch));
    patch[0] = 0x48;
    patch[1] = 0xB8;
    *reinterpret_cast<void**>(&patch[2]) = destination;
    patch[10] = 0xFF;
    patch[11] = 0xE0;

    memcpy(address, patch, patchLength);
    return true;
}

static void* CreateTrampoline(void* target, const BYTE* original, SIZE_T patchLength)
{
    if (!target || !original || patchLength < 12) return nullptr;

    SIZE_T trampolineSize = patchLength + 12;
    BYTE* trampoline = static_cast<BYTE*>(VirtualAlloc(nullptr, trampolineSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    if (!trampoline) return nullptr;

    memcpy(trampoline, original, patchLength);

    BYTE* jumpBack = trampoline + patchLength;
    jumpBack[0] = 0x48;
    jumpBack[1] = 0xB8;
    *reinterpret_cast<void**>(&jumpBack[2]) = static_cast<BYTE*>(target) + patchLength;
    jumpBack[10] = 0xFF;
    jumpBack[11] = 0xE0;

    FlushInstructionCache(GetCurrentProcess(), trampoline, trampolineSize);
    return trampoline;
}

static InlineHookEntry* FindInlineHookEntry(void* target)
{
    auto it = std::find_if(g_InlineHooks.begin(), g_InlineHooks.end(),
        [target](const InlineHookEntry& entry) { return entry.target == target; });
    return it == g_InlineHooks.end() ? nullptr : &(*it);
}

static bool InstallInlineHookLocked(void* target, void* detour, void** trampolineOut)
{
    if (!target || !detour) return false;

    InlineHookEntry* existing = FindInlineHookEntry(target);
    if (existing)
    {
        if (trampolineOut) *trampolineOut = existing->trampoline;
        return true;
    }

    InlineHookEntry entry = {};
    entry.target = target;
    entry.detour = detour;
    entry.patchLength = 12;

    if (!IsLikelySafeInlinePatchTarget(target, entry.patchLength))
    {
        return false;
    }

    memcpy(entry.original, target, entry.patchLength);

    entry.trampoline = CreateTrampoline(target, entry.original, entry.patchLength);
    if (!entry.trampoline)
    {
        return false;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(target, entry.patchLength, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        VirtualFree(entry.trampoline, 0, MEM_RELEASE);
        return false;
    }

    bool wrote = WriteAbsoluteJump(target, detour, entry.patchLength);

    DWORD dummy = 0;
    VirtualProtect(target, entry.patchLength, oldProtect, &dummy);
    FlushInstructionCache(GetCurrentProcess(), target, entry.patchLength);

    if (!wrote)
    {
        VirtualFree(entry.trampoline, 0, MEM_RELEASE);
        return false;
    }

    g_InlineHooks.push_back(entry);
    if (trampolineOut) *trampolineOut = entry.trampoline;
    return true;
}

static void ResolveOriginalXInputGetState()
{
    if (!g_OriginalGetProcAddress)
    {
        HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
        if (kernel32)
        {
            g_OriginalGetProcAddress = reinterpret_cast<GetProcAddressFn>(::GetProcAddress(kernel32, "GetProcAddress"));
        }
    }

    if (!g_OriginalXInputGetState14)
    {
        HMODULE mod = LoadLibraryW(L"xinput1_4.dll");
        if (mod)
        {
            g_OriginalXInputGetState14 = reinterpret_cast<XInputGetStateFn>(::GetProcAddress(mod, "XInputGetState"));
            g_OriginalXInputGetStateEx14 = reinterpret_cast<XInputGetStateFn>(::GetProcAddress(mod, reinterpret_cast<LPCSTR>(XINPUT_GET_STATE_EX_ORDINAL)));
        }
    }

    if (!g_OriginalXInputGetState910)
    {
        HMODULE mod = LoadLibraryW(L"xinput9_1_0.dll");
        if (mod)
        {
            g_OriginalXInputGetState910 = reinterpret_cast<XInputGetStateFn>(::GetProcAddress(mod, "XInputGetState"));
            g_OriginalXInputGetStateEx910 = reinterpret_cast<XInputGetStateFn>(::GetProcAddress(mod, reinterpret_cast<LPCSTR>(XINPUT_GET_STATE_EX_ORDINAL)));
        }
    }

    if (!g_OriginalXInputGetState13)
    {
        HMODULE mod = LoadLibraryW(L"xinput1_3.dll");
        if (mod)
        {
            g_OriginalXInputGetState13 = reinterpret_cast<XInputGetStateFn>(::GetProcAddress(mod, "XInputGetState"));
            g_OriginalXInputGetStateEx13 = reinterpret_cast<XInputGetStateFn>(::GetProcAddress(mod, reinterpret_cast<LPCSTR>(XINPUT_GET_STATE_EX_ORDINAL)));
        }
    }
}

static DWORD CallOriginalXInputGetState(DWORD userIndex, XINPUT_STATE* state)
{
    if (g_TrampolineXInputGetState14)
        return g_TrampolineXInputGetState14(userIndex, state);
    if (g_TrampolineXInputGetState910)
        return g_TrampolineXInputGetState910(userIndex, state);
    if (g_TrampolineXInputGetState13)
        return g_TrampolineXInputGetState13(userIndex, state);

    if (g_OriginalXInputGetState14)
        return g_OriginalXInputGetState14(userIndex, state);
    if (g_OriginalXInputGetState910)
        return g_OriginalXInputGetState910(userIndex, state);
    if (g_OriginalXInputGetState13)
        return g_OriginalXInputGetState13(userIndex, state);
    if (state)
    {
        ZeroMemory(state, sizeof(XINPUT_STATE));
    }
    return ERROR_DEVICE_NOT_CONNECTED;
}

static DWORD CallOriginalXInputGetStateEx(DWORD userIndex, XINPUT_STATE* state)
{
    if (g_TrampolineXInputGetStateEx14)
        return g_TrampolineXInputGetStateEx14(userIndex, state);
    if (g_TrampolineXInputGetStateEx910)
        return g_TrampolineXInputGetStateEx910(userIndex, state);
    if (g_TrampolineXInputGetStateEx13)
        return g_TrampolineXInputGetStateEx13(userIndex, state);

    if (g_OriginalXInputGetStateEx14)
        return g_OriginalXInputGetStateEx14(userIndex, state);
    if (g_OriginalXInputGetStateEx910)
        return g_OriginalXInputGetStateEx910(userIndex, state);
    if (g_OriginalXInputGetStateEx13)
        return g_OriginalXInputGetStateEx13(userIndex, state);
    return CallOriginalXInputGetState(userIndex, state);
}

extern "C" DWORD WINAPI HookedXInputGetState(DWORD userIndex, XINPUT_STATE* state)
{
    if (InterlockedCompareExchange(&g_XInputFilteringEnabled, 0, 0) != 0)
    {
        if (state)
        {
            ZeroMemory(state, sizeof(XINPUT_STATE));
        }
        return ERROR_SUCCESS;
    }
    return CallOriginalXInputGetState(userIndex, state);
}

extern "C" DWORD WINAPI HookedXInputGetStateEx(DWORD userIndex, XINPUT_STATE* state)
{
    if (InterlockedCompareExchange(&g_XInputFilteringEnabled, 0, 0) != 0)
    {
        if (state)
        {
            ZeroMemory(state, sizeof(XINPUT_STATE));
        }
        return ERROR_SUCCESS;
    }
    return CallOriginalXInputGetStateEx(userIndex, state);
}

extern "C" FARPROC WINAPI HookedGetProcAddress(HMODULE hModule, LPCSTR lpProcName)
{
    if (InterlockedCompareExchange(&g_XInputFilteringEnabled, 0, 0) != 0)
    {
        if (lpProcName && IsXInputModuleHandle(hModule))
        {
            ULONG_PTR ordinal = reinterpret_cast<ULONG_PTR>(lpProcName);
            if (ordinal <= 0xFFFF)
            {
                if (static_cast<WORD>(ordinal) == XINPUT_GET_STATE_EX_ORDINAL)
                {
                    return reinterpret_cast<FARPROC>(&HookedXInputGetStateEx);
                }
            }
            else if (std::strcmp(lpProcName, "XInputGetState") == 0)
            {
                return reinterpret_cast<FARPROC>(&HookedXInputGetState);
            }
            else if (std::strcmp(lpProcName, "XInputGetStateEx") == 0)
            {
                return reinterpret_cast<FARPROC>(&HookedXInputGetStateEx);
            }
        }
    }

    if (g_OriginalGetProcAddress)
    {
        return g_OriginalGetProcAddress(hModule, lpProcName);
    }
    return ::GetProcAddress(hModule, lpProcName);
}

static void PatchModuleXInputIat(HMODULE module)
{
    if (!module || module == g_SelfModule) return;

    BYTE* base = reinterpret_cast<BYTE*>(module);
    auto* dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (!dosHeader || dosHeader->e_magic != IMAGE_DOS_SIGNATURE) return;

    auto* ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);
    if (!ntHeaders || ntHeaders->Signature != IMAGE_NT_SIGNATURE) return;

    const IMAGE_DATA_DIRECTORY importDir = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (importDir.VirtualAddress == 0) return;

    auto* importDesc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + importDir.VirtualAddress);
    for (; importDesc->Name != 0; ++importDesc)
    {
        const char* importedDllName = reinterpret_cast<const char*>(base + importDesc->Name);
        bool isXinputImport = IsXInputDllName(importedDllName);
        bool isKernelImport = IsKernelDllName(importedDllName);
        if (!isXinputImport && !isKernelImport)
            continue;

        auto* thunkIat = reinterpret_cast<IMAGE_THUNK_DATA*>(base + importDesc->FirstThunk);
        auto* thunkOrig = importDesc->OriginalFirstThunk
            ? reinterpret_cast<IMAGE_THUNK_DATA*>(base + importDesc->OriginalFirstThunk)
            : thunkIat;

        for (; thunkIat->u1.Function != 0; ++thunkIat, ++thunkOrig)
        {
            void* replacement = nullptr;

            if (isXinputImport && IMAGE_SNAP_BY_ORDINAL(thunkOrig->u1.Ordinal))
            {
                WORD ordinal = IMAGE_ORDINAL(thunkOrig->u1.Ordinal);
                if (ordinal == XINPUT_GET_STATE_EX_ORDINAL)
                {
                    replacement = reinterpret_cast<void*>(&HookedXInputGetStateEx);
                }
            }
            else
            {
                auto* importByName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + thunkOrig->u1.AddressOfData);
                if (!importByName)
                    continue;

                const char* importName = reinterpret_cast<const char*>(importByName->Name);
                if (isXinputImport && std::strcmp(importName, "XInputGetState") == 0)
                {
                    replacement = reinterpret_cast<void*>(&HookedXInputGetState);
                }
                else if (isXinputImport && std::strcmp(importName, "XInputGetStateEx") == 0)
                {
                    replacement = reinterpret_cast<void*>(&HookedXInputGetStateEx);
                }
                else if (isKernelImport && std::strcmp(importName, "GetProcAddress") == 0)
                {
                    replacement = reinterpret_cast<void*>(&HookedGetProcAddress);
                }
            }

            if (!replacement)
                continue;

            void** slot = reinterpret_cast<void**>(&thunkIat->u1.Function);
            if (g_PatchedSlots.find(slot) != g_PatchedSlots.end())
                continue;

            DWORD oldProtect = 0;
            if (!VirtualProtect(slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
                continue;

            void* original = *slot;
            *slot = replacement;

            DWORD dummy = 0;
            VirtualProtect(slot, sizeof(void*), oldProtect, &dummy);
            FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));

            g_IatPatches.push_back({ slot, original });
            g_PatchedSlots.insert(slot);
        }
    }
}

static bool ApplyXInputIatHooksLocked()
{
    ResolveOriginalXInputGetState();
    if (!g_OriginalXInputGetState14 && !g_OriginalXInputGetState910 && !g_OriginalXInputGetState13)
        return false;

    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE)
        return false;

    MODULEENTRY32 moduleEntry;
    moduleEntry.dwSize = sizeof(moduleEntry);
    if (Module32First(snapshot, &moduleEntry))
    {
        do
        {
            PatchModuleXInputIat(moduleEntry.hModule);
        } while (Module32Next(snapshot, &moduleEntry));
    }

    CloseHandle(snapshot);
    return true;
}

static bool ApplyXInputInlineHooksLocked()
{
    ResolveOriginalXInputGetState();

    // Minimal inline scope: hook at most one GetState and one GetStateEx target.
    if (!g_TrampolineXInputGetState14 && g_OriginalXInputGetState14)
    {
        InstallInlineHookLocked(reinterpret_cast<void*>(g_OriginalXInputGetState14), reinterpret_cast<void*>(&HookedXInputGetState), reinterpret_cast<void**>(&g_TrampolineXInputGetState14));
    }
    else if (!g_TrampolineXInputGetState910 && g_OriginalXInputGetState910)
    {
        InstallInlineHookLocked(reinterpret_cast<void*>(g_OriginalXInputGetState910), reinterpret_cast<void*>(&HookedXInputGetState), reinterpret_cast<void**>(&g_TrampolineXInputGetState910));
    }
    else if (!g_TrampolineXInputGetState13 && g_OriginalXInputGetState13)
    {
        InstallInlineHookLocked(reinterpret_cast<void*>(g_OriginalXInputGetState13), reinterpret_cast<void*>(&HookedXInputGetState), reinterpret_cast<void**>(&g_TrampolineXInputGetState13));
    }

    if (!g_TrampolineXInputGetStateEx14 && g_OriginalXInputGetStateEx14)
    {
        InstallInlineHookLocked(reinterpret_cast<void*>(g_OriginalXInputGetStateEx14), reinterpret_cast<void*>(&HookedXInputGetStateEx), reinterpret_cast<void**>(&g_TrampolineXInputGetStateEx14));
    }
    else if (!g_TrampolineXInputGetStateEx910 && g_OriginalXInputGetStateEx910)
    {
        InstallInlineHookLocked(reinterpret_cast<void*>(g_OriginalXInputGetStateEx910), reinterpret_cast<void*>(&HookedXInputGetStateEx), reinterpret_cast<void**>(&g_TrampolineXInputGetStateEx910));
    }
    else if (!g_TrampolineXInputGetStateEx13 && g_OriginalXInputGetStateEx13)
    {
        InstallInlineHookLocked(reinterpret_cast<void*>(g_OriginalXInputGetStateEx13), reinterpret_cast<void*>(&HookedXInputGetStateEx), reinterpret_cast<void**>(&g_TrampolineXInputGetStateEx13));
    }

    return g_TrampolineXInputGetState14
        || g_TrampolineXInputGetState910
        || g_TrampolineXInputGetState13
        || g_TrampolineXInputGetStateEx14
        || g_TrampolineXInputGetStateEx910
        || g_TrampolineXInputGetStateEx13;
}

static void RestoreXInputIatHooksLocked()
{
    for (const IatPatchEntry& patch : g_IatPatches)
    {
        if (!patch.slot) continue;

        DWORD oldProtect = 0;
        if (!VirtualProtect(patch.slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect))
            continue;

        *patch.slot = patch.original;

        DWORD dummy = 0;
        VirtualProtect(patch.slot, sizeof(void*), oldProtect, &dummy);
        FlushInstructionCache(GetCurrentProcess(), patch.slot, sizeof(void*));
    }

    g_IatPatches.clear();
    g_PatchedSlots.clear();
}

static void RestoreXInputInlineHooksLocked()
{
    for (const InlineHookEntry& hook : g_InlineHooks)
    {
        if (!hook.target || hook.patchLength == 0) continue;

        DWORD oldProtect = 0;
        if (!VirtualProtect(hook.target, hook.patchLength, PAGE_EXECUTE_READWRITE, &oldProtect))
            continue;

        memcpy(hook.target, hook.original, hook.patchLength);

        DWORD dummy = 0;
        VirtualProtect(hook.target, hook.patchLength, oldProtect, &dummy);
        FlushInstructionCache(GetCurrentProcess(), hook.target, hook.patchLength);

        if (hook.trampoline)
        {
            VirtualFree(hook.trampoline, 0, MEM_RELEASE);
        }
    }

    g_InlineHooks.clear();
    g_TrampolineXInputGetState14 = nullptr;
    g_TrampolineXInputGetState910 = nullptr;
    g_TrampolineXInputGetState13 = nullptr;
    g_TrampolineXInputGetStateEx14 = nullptr;
    g_TrampolineXInputGetStateEx910 = nullptr;
    g_TrampolineXInputGetStateEx13 = nullptr;
}

extern "C" __declspec(dllexport) DWORD SetXInputFilteringEnabled(int enabled)
{
    std::lock_guard<std::mutex> lock(g_XInputHookMutex);

    if (enabled != 0)
    {
        bool iatApplied = ApplyXInputIatHooksLocked();
        bool inlineApplied = false;
        bool hooksApplied = iatApplied || inlineApplied;
        InterlockedExchange(&g_XInputFilteringEnabled, hooksApplied ? 1 : 0);
        return hooksApplied ? ERROR_SUCCESS : ERROR_PROC_NOT_FOUND;
    }

    InterlockedExchange(&g_XInputFilteringEnabled, 0);
    RestoreXInputIatHooksLocked();
    RestoreXInputInlineHooksLocked();
    return ERROR_SUCCESS;
}

extern "C" __declspec(dllexport) DWORD GetFilteredXInputState(DWORD userIndex, XINPUT_STATE* state)
{
    std::lock_guard<std::mutex> lock(g_XInputHookMutex);
    ResolveOriginalXInputGetState();
    return CallOriginalXInputGetState(userIndex, state);
}

extern "C" __declspec(dllexport) void UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    IUnityGraphics* graphics = unityInterfaces->Get<IUnityGraphics>();
    if (graphics && graphics->GetRenderer() == kUnityGfxRendererD3D11)
    {
        IUnityGraphicsD3D11* d3d11 = unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (d3d11)
        {
            g_D3D11Device = d3d11->GetDevice();
            if (g_D3D11Device)
            {
                g_D3D11Device->GetImmediateContext(&s_ImmediateContext);
            }
        }
    }
}

extern "C" __declspec(dllexport) void UnityPluginUnload()
{
    SetXInputFilteringEnabled(0);

    if (s_ImmediateContext)
    {
        s_ImmediateContext->Release();
        s_ImmediateContext = nullptr;
    }
    g_D3D11Device = nullptr;
}

extern "C" __declspec(dllexport) void SetDevicePointerFromCSharp(void* deviceFromCSharp)
{
    ID3D11Device* newDevice = static_cast<ID3D11Device*>(deviceFromCSharp);
    if (newDevice != g_D3D11Device)
    {
        if (s_ImmediateContext)
        {
            s_ImmediateContext->Release();
            s_ImmediateContext = nullptr;
        }
        g_D3D11Device = newDevice;
        if (g_D3D11Device)
        {
            g_D3D11Device->GetImmediateContext(&s_ImmediateContext);
        }
    }
}

extern "C" __declspec(dllexport) void* GetD3D11Device()
{
    return g_D3D11Device;
}

extern "C" __declspec(dllexport) void* GetDeviceFromResource(void* pResource)
{
    if (!pResource) return nullptr;
    ID3D11Resource* d3d11Resource = static_cast<ID3D11Resource*>(pResource);
    ID3D11Device* d3d11Device = nullptr;
    d3d11Resource->GetDevice(&d3d11Device);
    return d3d11Device;
}

extern "C" __declspec(dllexport) void DirectCopyResource(void* pDest, void* pSrc)
{
    if (!s_ImmediateContext || !pDest || !pSrc) return;
    ID3D11Resource* pDestResource = static_cast<ID3D11Resource*>(pDest);
    ID3D11Resource* pSrcResource = static_cast<ID3D11Resource*>(pSrc);
    s_ImmediateContext->CopyResource(pDestResource, pSrcResource);
}

extern "C" __declspec(dllexport) HRESULT CreateAndRegisterSRV(void* pTextureResource, int srvFormatDXGI, void** ppSRV)
{
    if (!g_D3D11Device || !pTextureResource) 
    {
        if (ppSRV) *ppSRV = nullptr;
        return E_FAIL;
    }
    
    ID3D11Texture2D* pTexture2D = static_cast<ID3D11Texture2D*>(pTextureResource);
    D3D11_TEXTURE2D_DESC texDesc;
    pTexture2D->GetDesc(&texDesc);

    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = static_cast<DXGI_FORMAT>(srvFormatDXGI);
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MostDetailedMip = 0;
    srvDesc.Texture2D.MipLevels = (texDesc.MipLevels == 0) ? -1 : texDesc.MipLevels;

    ID3D11ShaderResourceView* pNewSRV = nullptr;
    HRESULT hr = g_D3D11Device->CreateShaderResourceView(pTexture2D, &srvDesc, &pNewSRV);

    if (SUCCEEDED(hr)) 
    {
        if (ppSRV) *ppSRV = pNewSRV;
    } 
    else 
    {
        if (ppSRV) *ppSRV = nullptr;
    }
    return hr;
}

extern "C" __declspec(dllexport) void ReleaseNativeObject(void* pObject)
{
    if (pObject)
    {
        ((IUnknown*)pObject)->Release();
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    (void)lpReserved;
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        g_SelfModule = hModule;
        break;
    case DLL_PROCESS_DETACH:
        g_SelfModule = nullptr;
        break;
    default:
        break;
    }
    return TRUE;
}