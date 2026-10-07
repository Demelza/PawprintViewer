/*
 * Radegast Metaverse Client
 * Copyright (c) 2026, Sjofn LLC
 * All rights reserved.
 *
 * Radegast is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

// Ported from Avalonia's own samples/GpuInterop/VulkanDemo's VulkanSemaphorePair.cs (MIT
// licensed, https://github.com/AvaloniaUI/Avalonia). Used by VkInteropSwapchain's Mode A path
// (native Vulkan compositor, ExternalMemoryHandleTypeFlags.OpaqueWin32Bit images -- see
// VkInteropImage's per-platform PNext branch) -- Mode B's D3D11-backed images use the Win32
// keyed-mutex protocol instead (see VkCommandBufferPool's KeyedMutexSubmitInfo), not this
// class. Kept for completeness/portability; also the active path on macOS (MTLSharedEvent via
// VK_EXT_metal_objects) and on Linux (opaque FD via VK_KHR_external_semaphore_fd).

using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Radegast.Veles.Rendering;

internal sealed unsafe class VkSemaphorePair : IDisposable
{
    private readonly VkContext _vk;

    public VkSemaphorePair(VkContext vk, bool exportable)
    {
        _vk = vk;

        void* createPNext = null;

        // On macOS, binary semaphores use id<MTLEvent> internally in MoltenVK, which exports
        // as null from ExportMetalSharedEventInfoEXT (MTLEvent ≠ MTLSharedEvent). Timeline
        // semaphores use id<MTLSharedEvent> and export correctly. Build the pNext chain:
        //   SemaphoreTypeCreateInfo (timeline) → ExportMetalObjectCreateInfoEXT (MTLSharedEvent)
        var exportMetalInfo = new ExportMetalObjectCreateInfoEXT
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.SharedEventBitExt
        };
        var timelineInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
            PNext = &exportMetalInfo
        };

        var semaphoreExportInfo = new ExportSemaphoreCreateInfo
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit
                : ExternalSemaphoreHandleTypeFlags.OpaqueFDBit
        };

        if (exportable)
            createPNext = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (void*)&timelineInfo
                : (void*)&semaphoreExportInfo;

        var semaphoreCreateInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = createPNext
        };

        vk.Api.CreateSemaphore(vk.Device, in semaphoreCreateInfo, null, out var semaphore).ThrowOnError();
        ImageAvailableSemaphore = semaphore;

        vk.Api.CreateSemaphore(vk.Device, in semaphoreCreateInfo, null, out semaphore).ThrowOnError();
        RenderFinishedSemaphore = semaphore;
    }

    private int ExportFd(bool renderFinished)
    {
        if (!_vk.Api.TryGetDeviceExtension<KhrExternalSemaphoreFd>(_vk.Instance, _vk.Device, out var ext))
            throw new InvalidOperationException();
        var info = new SemaphoreGetFdInfoKHR
        {
            SType = StructureType.SemaphoreGetFDInfoKhr,
            Semaphore = renderFinished ? RenderFinishedSemaphore : ImageAvailableSemaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit
        };
        ext.GetSemaphoreF(_vk.Device, in info, out var fd).ThrowOnError();
        return fd;
    }

    private IntPtr ExportWin32(bool renderFinished)
    {
        if (!_vk.Api.TryGetDeviceExtension<KhrExternalSemaphoreWin32>(_vk.Instance, _vk.Device, out var ext))
            throw new InvalidOperationException();
        var info = new SemaphoreGetWin32HandleInfoKHR
        {
            SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
            Semaphore = renderFinished ? RenderFinishedSemaphore : ImageAvailableSemaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit
        };
        ext.GetSemaphoreWin32Handle(_vk.Device, in info, out var fd).ThrowOnError();
        return fd;
    }

    public IPlatformHandle Export(bool renderFinished)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new PlatformHandle(ExportWin32(renderFinished), KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new PlatformHandle(ExportMTLSharedEvent(renderFinished), KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent);
        return new PlatformHandle(new IntPtr(ExportFd(renderFinished)), KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor);
    }

    private nint ExportMTLSharedEvent(bool renderFinished)
    {
        if (!_vk.Api.TryGetDeviceExtension<ExtMetalObjects>(_vk.Instance, _vk.Device, out var ext))
            throw new InvalidOperationException("VK_EXT_metal_objects not available");
        var sharedEventInfo = new ExportMetalSharedEventInfoEXT
        {
            SType = StructureType.ExportMetalSharedEventInfoExt,
            Semaphore = renderFinished ? RenderFinishedSemaphore : ImageAvailableSemaphore
        };
        var exportObjects = new ExportMetalObjectsInfoEXT
        {
            SType = StructureType.ExportMetalObjectsInfoExt,
            PNext = &sharedEventInfo
        };
        ext.ExportMetalObjects(_vk.Device, &exportObjects);
        return sharedEventInfo.MtlSharedEvent;
    }

    internal Semaphore ImageAvailableSemaphore { get; }
    internal Semaphore RenderFinishedSemaphore { get; }

    public void Dispose()
    {
        _vk.Api.DestroySemaphore(_vk.Device, ImageAvailableSemaphore, null);
        _vk.Api.DestroySemaphore(_vk.Device, RenderFinishedSemaphore, null);
    }
}
