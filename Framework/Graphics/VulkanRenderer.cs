using Brutal;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.Pointers.Extensions;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.TextureApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using CommunityToolkit.HighPerformance;
using System.Resources;

namespace BrutalSandbox;

public sealed unsafe class VulkanRenderer : IRenderer
{
    private const int MaxFramesInFlight = 2;
    private const int PushConstantSize = 192; // one mat4

    private readonly GlfwWindow _window;
    private readonly Instance _instance;
    private readonly VkSurfaceKHR _surface;
    private readonly PhysicalDevice _physical;
    private readonly DeviceEx _device;
    private readonly Queue _queue;
    private readonly int _queueFamily;
    private readonly Frame[] _frames;

    private VkSwapchainKHR _swapchain;
    private VkFormat _format;
    private VkExtent2D _extent;
    private VkImage[] _swapchainImages = [];
    private VkImageView[] _imageViews = [];
    private VkFramebuffer[] _framebuffers = [];
    private VkFormat _depthFormat;
    private VkImage[] _depthImages = [];
    private VkDeviceMemory[] _depthMemories = [];
    private VkImageView[] _depthViews = [];

    private VkRenderPass _renderPass;
    private VkPipelineLayout _pipelineLayout;
    private VkPipeline _pipeline;

    private VkBuffer _vertexBuffer;
    private VkDeviceMemory _vertexMemory;
    private int _vertexCount;
    private VkBuffer _indexBuffer;
    private VkDeviceMemory _indexMemory;
    private VkDescriptorSetLayout _descriptorSetLayout;
    private VkDescriptorPool _descriptorPool;
    private VkDescriptorSet _descriptorSet;
    public struct VulkanTextureResources
    {
    public VkImage Image;
    public VkDeviceMemory Memory;
    public VkImageView View;
    }

    private VulkanTextureResources _meshTexture;
    private VkSampler _textureSampler;

    // Пул дескрипторов для нашего меша (выделяем отдельно от ImGui)
    private VkDescriptorPool _meshDescriptorPool;
    private int _indexCount;

    private int _currentFrame;

    private readonly VulkanBackend _imGuiVulkanBackend;
    private sealed class Frame
    {
        public VkCommandPool Pool;
        public CommandBuffer CommandBuffer;
        public VkFence Fence;
        public VkSemaphore ImageAvailable;
        public VkSemaphore RenderFinished;
    }

    public VulkanRenderer(GlfwWindow window)
    {
        _window = window;

        _instance = CreateInstance();
        _surface = CreateSurface(window);

        (_physical, _queueFamily) = PickPhysicalDevice();
        _device = CreateDevice();
        _queue = _device.GetQueue(_queueFamily, 0);

        _frames = new Frame[MaxFramesInFlight];
        for (var i = 0; i < _frames.Length; i++)
            _frames[i] = CreateFrame();

        CreateSwapchain();
        CreatePipelineResources();

        // Инициализируем Vulkan-бэкенд для ImGui.
        // Передаем все необходимые дескрипторы и параметры рендер-пасса.
        _imGuiVulkanBackend = new VulkanBackend(new VulkanBackend.CreateInfo
        {
            Device = _device,
            GraphicsQueue = _queue,
            RenderPass = _renderPass,
            MinImageCount = 2,
            ImageCount = _swapchainImages.Length,
            SampleCount = VkSampleCountFlags._1Bit,
            DescriptorPoolSize = 1000 // Задаем обязательный размер пула
        });
    }

    // Хелпер для создания Descriptor Pool, который необходим ImGui для связывания текстур/шрифтов
    private VkDescriptorPool CreateImGuiDescriptorPool()
    {
        Span<VkDescriptorPoolSize> poolSizes = [
            new VkDescriptorPoolSize { Type = VkDescriptorType.Sampler, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.SampledImage, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.UniformTexelBuffer, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageTexelBuffer, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBufferDynamic, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBufferDynamic, DescriptorCount = 1000 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.InputAttachment, DescriptorCount = 1000 }
        ];

        return _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
        {
            Flags = VkDescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = 1000 * poolSizes.Length,
            PoolSizeCount = poolSizes.Length,
            PoolSizes = poolSizes.AsPointer()
        }, null);
    }


    public int Width => _extent.Width;
    public int Height => _extent.Height;
    public bool HasMesh => _indexCount > 0;

    public readonly Dictionary<string, (VkImageView View, VkDescriptorSet Set, VkImage Image, VkDeviceMemory Memory)> _vulkanAllocatedTextures = new(StringComparer.OrdinalIgnoreCase);
    // Ссылка на дескриптор, который должен быть привязан при отрисовке текущего меша
    private VkDescriptorSet _activeDescriptorSet = default;

    private CommandBuffer _currentCmd;
    private int _currentImageIndex;
    private float4x4 _currentViewProj;


    private VkPipeline _lastBoundPipeline = default;
    private VkBuffer _lastBoundVbo = default;
    private VkDescriptorSet _lastBoundDescriptorSet = default;
    public void BeginFrame(float4x4 viewProjection)
    {
        if (_extent.Width <= 0 || _extent.Height <= 0) return;

        var frame = _frames[_currentFrame];
        _device.WaitForFences([frame.Fence], true, -1);

        var imageAvailable = frame.ImageAvailable;
        var swapchain = _swapchain;

        var acquire = _device.AcquireNextImageKHR(swapchain, -1, imageAvailable, default, out _currentImageIndex);
        if (acquire == VkResult.ErrorOutOfDateKHR)
        {
            RecreateSwapchain();
            return;
        }

        _device.ResetFences([frame.Fence]);
        _device.ResetCommandPool(frame.Pool, VkCommandPoolResetFlags.None);

        _currentCmd = frame.CommandBuffer;
        _currentCmd.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });

        var colorClear = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(0.05f, 0.06f, 0.08f, 1f) } };
        var depthClear = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1f, Stencil = 0 } };
        Span<VkClearValue> clearValues = [colorClear, depthClear];

        _currentCmd.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = _renderPass,
                Framebuffer = _framebuffers[_currentImageIndex],
                RenderArea = new VkRect2D { Extent = _extent },
                ClearValueCount = clearValues.Length,
                ClearValues = clearValues.AsPointer(),
            },
            VkSubpassContents.Inline
        );

        _currentViewProj = viewProjection;

        _lastBoundPipeline = default;
        _lastBoundVbo = default;
        _lastBoundDescriptorSet = default;
    }
    public ImTextureRef GetImGuiTexture(string assetName)
    {
        if (_vulkanAllocatedTextures.TryGetValue(assetName, out var allocated))
        {
            // Превращаем сохраненный VkDescriptorSet обратно в ImTextureRef через ImTextureID
            return new ImTextureRef { _TexID = new ImTextureID((nint)allocated.Set.VkHandle) };
        }
        return default;
    }
    public void UpdateViewProjection(float4x4 viewProjection)
    {
        _currentViewProj = viewProjection;
    }
    public void RenderMesh(MeshData data, float4x4 model, string assetName)
    {
        if (data.Vertices.Length == 0 || data.Indices.Length == 0) return;

        if (data.VkBuffers is null)
        {
            var (vbo, vboMem) = CreateHostVisibleBuffer(data.Vertices, VkBufferUsageFlags.VertexBufferBit);
            var (ibo, iboMem) = CreateHostVisibleBuffer(data.Indices, VkBufferUsageFlags.IndexBufferBit);
            data.VkBuffers = (vbo, vboMem, ibo, iboMem);
        }

        var buffers = ((VkBuffer Vbo, VkDeviceMemory VboMem, VkBuffer Ibo, VkDeviceMemory IboMem))data.VkBuffers;

        // ОПТИМИЗАЦИЯ: Биндим пайплайн, viewport и scissor ТОЛЬКО ОДИН РАЗ за весь кадр!
        if (_lastBoundPipeline.VkHandle != _pipeline.VkHandle)
        {
            _currentCmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            _currentCmd.SetViewport(0, [new VkViewport { Width = _extent.Width, Height = _extent.Height, MaxDepth = 1f }]);
            _currentCmd.SetScissor(0, [new VkRect2D { Extent = _extent }]);
            _lastBoundPipeline = _pipeline;
        }

        // ОПТИМИЗАЦИЯ: Биндим текстуру только если она реально изменилась
        if (_vulkanAllocatedTextures.TryGetValue(assetName, out var allocated))
        {
            if (_lastBoundDescriptorSet.VkHandle != allocated.Set.VkHandle)
            {
                _currentCmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0, [allocated.Set], []);
                _lastBoundDescriptorSet = allocated.Set;
            }
        }

        // ОПТИМИЗАЦИЯ: Биндим геометрию меша только если мы перешли к другому типу объекта
        if (_lastBoundVbo.VkHandle != buffers.Vbo.VkHandle)
        {
            _currentCmd.BindVertexBuffer(0, buffers.Vbo);
            _currentCmd.BindIndexBuffer(buffers.Ibo, ByteSize.Zero, VkIndexType.UInt32);
            _lastBoundVbo = buffers.Vbo;
        }

        float4x4.Invert(model, out float4x4 invertedModel);
        float4x4 normalMatrix = float4x4.Transpose(invertedModel);

        float4x4 mvp = model * _currentViewProj;

        // Пушим последовательно байты всех трех матриц в один вызов PushConstants
        _currentCmd.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, mvp);
        _currentCmd.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, (ByteSize64)64, model);
        _currentCmd.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, (ByteSize64)128, normalMatrix);

        _currentCmd.DrawIndexed(data.Indices.Length, 1, 0, 0, 0);
    }


    public void EndFrame(Action? postDraw = null)
    {
        var frame = _frames[_currentFrame];

        if (postDraw != null)
        {
            _imGuiVulkanBackend.RenderDrawData(ImGui.GetDrawData(), _currentCmd);
        }

        _currentCmd.EndRenderPass();
        _currentCmd.End();

        var waitStage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        var handle = _currentCmd.Handle;
        var imageAvailable = frame.ImageAvailable;
        var renderFinished = frame.RenderFinished;

        _queue.Submit([new VkSubmitInfo {
        WaitSemaphoreCount = 1, WaitSemaphores = &imageAvailable, WaitDstStageMask = &waitStage,
        CommandBufferCount = 1, CommandBuffers = &handle,
        SignalSemaphoreCount = 1, SignalSemaphores = &renderFinished,
    }], frame.Fence);

        var swapchain = _swapchain;
        var presentIndex = _currentImageIndex;
        var present = _queue.PresentKHR(new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &presentIndex,
        });

        if (present is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR) RecreateSwapchain();

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }
    public unsafe void InitializeTextureOnDevice(string assetName, ITexture texture)
    {
        var width = (uint)texture.Extent.X;
        var height = (uint)texture.Extent.Y;
        var vkFormat = (VkFormat)(int)texture.Format;

        // 1. Создание VkImage
        VkImage image = _device.CreateImage(new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = vkFormat,
            Extent = new VkExtent3D { Width = (int)width, Height = (int)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined
        }, null);

        var requirements = _device.GetImageMemoryRequirements(image);
        var memoryType = _physical.FindMemoryType((int)requirements.MemoryTypeBits, VkMemoryPropertyFlags.DeviceLocalBit);

        VkDeviceMemory memory = _device.AllocateMemory(new VkMemoryAllocateInfo
        {
            AllocationSize = (ByteSize)requirements.Size,
            MemoryTypeIndex = memoryType
        }, null);

        _device.BindImageMemory(image, memory, ByteSize.Zero);

        // 2. Копирование данных через Staging буфер
        var rawData = texture.ImageData(0, 0, 0);
        var stagingBuffer = _device.CreateBuffer(new VkBufferCreateInfo
        {
            Size = (ByteSize)rawData.Length,
            Usage = VkBufferUsageFlags.TransferSrcBit,
            SharingMode = VkSharingMode.Exclusive
        }, null);

        var stagingReq = _device.GetBufferMemoryRequirements(stagingBuffer);
        var stagingMemType = _physical.FindMemoryType((int)stagingReq.MemoryTypeBits, VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit);
        VkDeviceMemory stagingMemory = _device.AllocateMemory(new VkMemoryAllocateInfo { AllocationSize = stagingReq.Size, MemoryTypeIndex = stagingMemType }, null);

        _device.BindBufferMemory(stagingBuffer, stagingMemory, ByteSize.Zero);
        _device.WriteMemory(rawData, stagingMemory, ByteSize.Zero);

        var transientPool = _device.CreateCommandPool(new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlags.TransientBit, QueueFamilyIndex = _queueFamily }, null);
        var cmd = _device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = transientPool, Level = VkCommandBufferLevel.Primary });

        cmd.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });

        var barrier1 = new VkImageMemoryBarrier
        {
            OldLayout = VkImageLayout.Undefined,
            NewLayout = VkImageLayout.TransferDstOptimal,
            SrcAccessMask = 0,
            DstAccessMask = VkAccessFlags.TransferWriteBit,
            Image = image,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [], [barrier1]);

        var region = new VkBufferImageCopy
        {
            BufferOffset = (ByteSize64)0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = (int)width, Height = (int)height, Depth = 1 }
        };
        cmd.CopyBufferToImage(stagingBuffer, image, VkImageLayout.TransferDstOptimal, [region]);

        var barrier2 = new VkImageMemoryBarrier
        {
            OldLayout = VkImageLayout.TransferDstOptimal,
            NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = VkAccessFlags.TransferWriteBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit,
            Image = image,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [], [], [barrier2]);
        cmd.End();

        var handle = cmd.Handle;
        VkFence nullFence = default;
        _queue.Submit([new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &handle }], nullFence);
        _device.WaitIdle();

        _device.DestroyBuffer(stagingBuffer, null);
        _device.FreeMemory(stagingMemory, null);
        _device.DestroyCommandPool(transientPool, null);

        // 3. Создание VkImageView
        VkImageView view = _device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = vkFormat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
        }, null);

        // === МАГИЯ БОРЬБЫ С КРАШЕМ: Используем проверенный AddTexture из ImGuiBackend ===
        // Вызываем встроенную регистрацию текстуры. Внутри неё Brutal безопасно создаёт рабочий DescriptorSet.
        var imGuiTexRef = _imGuiVulkanBackend.AddTexture(_textureSampler, view, VkImageLayout.ShaderReadOnlyOptimal);

        // Превращаем полученный нативный хэндл обратно в структуру VkDescriptorSet для рендерера
        VkDescriptorSet texDescriptorSet = new VkDescriptorSet((nint)imGuiTexRef._TexID.Value);

        // Сохраняем ресурсы в словарь для последующего бинда в Draw кадра
        _vulkanAllocatedTextures[assetName] = (view, texDescriptorSet, image, memory);
        Console.WriteLine($"[Vulkan GPU] Текстура '{assetName}' ({width}x{height}) успешно зарегистрирована через ImGui-пул.");
    }



    public void BindMaterialTexture(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return;

        if (_vulkanAllocatedTextures.TryGetValue(assetName, out var allocated))
        {
            // Переключаем активный сет дескрипторов на тот, что привязан к этой текстуре
            _activeDescriptorSet = allocated.Set;
        }
    }
    public void SetMesh(MeshData data)
    {
        _device.WaitIdle();
        DestroyMesh();

        if (data.Vertices.Length == 0 || data.Indices.Length == 0)
        {
            Console.WriteLine($"'{data.Name}' contains no indexed triangles.");
            return;
        }

        (_vertexBuffer, _vertexMemory) = CreateHostVisibleBuffer(data.Vertices, VkBufferUsageFlags.VertexBufferBit);
        (_indexBuffer, _indexMemory) = CreateHostVisibleBuffer(data.Indices, VkBufferUsageFlags.IndexBufferBit);
        _vertexCount = data.Vertices.Length;
        _indexCount = data.Indices.Length;

        Console.WriteLine($"Loaded '{data.Name}': {_vertexCount} vertices, {data.TriangleCount} triangles.");
    }

    public void Resize(int width, int height)
    {
        if (width == _extent.Width && height == _extent.Height)
            return;
        RecreateSwapchain();
    }

    public void Draw(float4x4 mvp, Action? postDraw = null)
    {
        if (_extent.Width <= 0 || _extent.Height <= 0)
            return;

        var frame = _frames[_currentFrame];
        _device.WaitForFences([frame.Fence], true, -1);

        var imageAvailable = frame.ImageAvailable;
        var renderFinished = frame.RenderFinished;
        var swapchain = _swapchain;

        var acquire = _device.AcquireNextImageKHR(swapchain, -1, imageAvailable, default, out var imageIndex);
        if (acquire == VkResult.ErrorOutOfDateKHR)
        {
            RecreateSwapchain();
            return;
        }
        if (acquire is not VkResult.Success and not VkResult.SuboptimalKHR)
            throw new Exception($"Failed to acquire swapchain image: {acquire}");

        _device.ResetFences([frame.Fence]);
        _device.ResetCommandPool(frame.Pool, VkCommandPoolResetFlags.None);

        var commandBuffer = frame.CommandBuffer;
        commandBuffer.Begin(new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit });

        var colorClear = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(0.05f, 0.06f, 0.08f, 1f) } };
        var depthClear = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1f, Stencil = 0 } };
        Span<VkClearValue> clearValues = [colorClear, depthClear];

        commandBuffer.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = _renderPass,
                Framebuffer = _framebuffers[imageIndex],
                RenderArea = new VkRect2D { Extent = _extent },
                ClearValueCount = clearValues.Length,
                ClearValues = clearValues.AsPointer(),
            },
            VkSubpassContents.Inline
        );

        // 1. Отрисовка основной 3D геометрии игры в VulkanRenderer.Draw()
        if (_indexCount > 0)
        {
            commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            commandBuffer.SetViewport(0, [new VkViewport { Width = _extent.Width, Height = _extent.Height, MaxDepth = 1f }]);
            commandBuffer.SetScissor(0, [new VkRect2D { Extent = _extent }]);

            // ИСПРАВЛЕНИЕ: Биндим не пустой _descriptorSet, а _activeDescriptorSet конкретной текстуры!
            if (!_activeDescriptorSet.IsNull())
            {
                VkDescriptorSet setHandle = _activeDescriptorSet;
                commandBuffer.BindDescriptorSets(
                    VkPipelineBindPoint.Graphics,
                    _pipelineLayout,
                    0,
                    [setHandle],
                    []
                );
            }

            commandBuffer.BindVertexBuffer(0, _vertexBuffer);
            commandBuffer.BindIndexBuffer(_indexBuffer, ByteSize.Zero, VkIndexType.UInt32);
            commandBuffer.PushConstants(_pipelineLayout, VkShaderStageFlags.VertexBit, ByteSize.Zero, mvp);
            commandBuffer.DrawIndexed(_indexCount, 1, 0, 0, 0);
        }


        // 2. Внедрение ImGui команд. Рендеринг интерфейса происходит поверх 3D сцены
        // до закрытия текущего RenderPass.
        if (postDraw != null)
        {
            // Передаем нативный дескриптор буфера команд в бэкенд ImGui
            _imGuiVulkanBackend.RenderDrawData(ImGui.GetDrawData(), commandBuffer);
        }

        commandBuffer.EndRenderPass();
        commandBuffer.End();

        var waitStage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        var handle = commandBuffer.Handle;
        _queue.Submit([new VkSubmitInfo {
            WaitSemaphoreCount = 1, WaitSemaphores = &imageAvailable, WaitDstStageMask = &waitStage,
            CommandBufferCount = 1, CommandBuffers = &handle,
            SignalSemaphoreCount = 1, SignalSemaphores = &renderFinished,
        }], frame.Fence);

        var presentIndex = imageIndex;
        var present = _queue.PresentKHR(new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &presentIndex,
        });

        if (present is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            RecreateSwapchain();
        else if (present != VkResult.Success)
            throw new Exception($"Failed to present: {present}");

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    private Instance CreateInstance()
    {
        var extensions = new HashSet<string>();
        var required = Glfw.GetRequiredInstanceExtensions(out var count);
        if (required == null)
            throw new Exception("GLFW returned no Vulkan instance extensions; a Vulkan surface cannot be created here.");

        for (var i = 0u; i < count; i++)
            extensions.Add(Utf8.GetString(required[i]));

        // Adds the Metal portability extensions on macOS.
        VulkanHelpers.AddPlatformRequiredExtensions(extensions);

        return Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo
                {
                    ApplicationName = null,
                    ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3,
                },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = extensions,
                EnabledLayers = new HashSet<string>(),
            }
        );
    }

    private VkSurfaceKHR CreateSurface(GlfwWindow window)
    {
        ulong handle;
        if (Glfw.PInvoke.CreateWindowSurface(_instance.VkHandle, window, null, &handle) != 0)
            throw new Exception("Failed to create a Vulkan window surface (is a Vulkan driver installed?).");
        return new VkSurfaceKHR((nint)handle);
    }

    private (PhysicalDevice, int) PickPhysicalDevice()
    {
        foreach (var physicalDevice in _instance.PhysicalDevices)
        {
            var family = FindGraphicsPresentFamily(physicalDevice);
            if (family >= 0)
                return (physicalDevice, family);
        }

        throw new Exception("No Vulkan device with a graphics + present queue was found.");
    }

    private int FindGraphicsPresentFamily(PhysicalDevice physicalDevice)
    {
        var familyCount = physicalDevice.GetQueueFamilyProperties_Count();
        Span<VkQueueFamilyProperties> families = stackalloc VkQueueFamilyProperties[familyCount];
        physicalDevice.GetQueueFamilyProperties_Write(families);

        for (var i = 0; i < families.Length; i++)
        {
            if (!families[i].QueueFlags.HasFlag(VkQueueFlags.GraphicsBit))
                continue;
            if (!physicalDevice.GetSurfaceSupportKHR(i, _surface))
                continue;
            return i;
        }

        return -1;
    }

    private DeviceEx CreateDevice()
    {
        float* priority = stackalloc float[1];
        priority[0] = 1f;

        var queueInfo = new VkDeviceQueueCreateInfo
        {
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            QueuePriorities = priority,
        };

        var extensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(extensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(extensions, _physical);

        return _physical.CreateDevice(
            new DeviceEx.CreateInfo { Extensions = extensions, QueueCreateInfos = [queueInfo] }
        );
    }

    private Frame CreateFrame()
    {
        var pool = _device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = _queueFamily }, null);
        var commandBuffer = _device.AllocateCommandBuffer(
            new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }
        );

        return new Frame
        {
            Pool = pool,
            CommandBuffer = commandBuffer,
            Fence = _device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null),
            ImageAvailable = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null),
            RenderFinished = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null),
        };
    }

    private void CreateSwapchain()
    {
        var capabilities = _physical.GetSurfaceCapabilitiesKHR(_surface);
        var surfaceFormat = SelectSurfaceFormat();
        var presentMode = SelectPresentMode();

        var minImageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount != 0 && minImageCount > capabilities.MaxImageCount)
            minImageCount = capabilities.MaxImageCount;

        var extent = capabilities.CurrentExtent;
        if (extent.Width == -1)
        {
            var framebufferSize = _window.FramebufferSize;
            extent.Width = Math.Clamp(framebufferSize.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
            extent.Height = Math.Clamp(framebufferSize.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);
        }

        _swapchain = _device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = _surface,
                MinImageCount = minImageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = capabilities.SupportedTransforms.HasFlag(VkSurfaceTransformFlagsKHR.IdentityBitKHR)
                    ? VkSurfaceTransformFlagsKHR.IdentityBitKHR
                    : capabilities.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = presentMode,
                Clipped = true,
            },
            null
        );

        _format = surfaceFormat.Format;
        _extent = extent;

        var imageCount = _device.GetSwapchainImagesKHR_Count(_swapchain);
        _swapchainImages = new VkImage[imageCount];
        _device.GetSwapchainImagesKHR_Write(_swapchain, _swapchainImages);

        _imageViews = new VkImageView[imageCount];
        _framebuffers = new VkFramebuffer[imageCount];
        _depthImages = new VkImage[imageCount];
        _depthMemories = new VkDeviceMemory[imageCount];
        _depthViews = new VkImageView[imageCount];
    }

    private void CreatePipelineResources()
    {
        _depthFormat = FindDepthFormat();
        _renderPass = CreateRenderPass();

        for (var i = 0; i < _imageViews.Length; i++)
        {
            _imageViews[i] = _device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = _swapchainImages[i],
                    ViewType = VkImageViewType._2D,
                    Format = _format,
                    Components = new VkComponentMapping
                    {
                        R = VkComponentSwizzle.R,
                        G = VkComponentSwizzle.G,
                        B = VkComponentSwizzle.B,
                        A = VkComponentSwizzle.A,
                    },
                    SubresourceRange = new VkImageSubresourceRange
                    {
                        AspectMask = VkImageAspectFlags.ColorBit,
                        LevelCount = 1,
                        LayerCount = 1,
                    },
                },
                null
            );

            CreateDepthResources(i);

            Span<VkImageView> attachments = [_imageViews[i], _depthViews[i]];
            _framebuffers[i] = _device.CreateFramebuffer(
                new VkFramebufferCreateInfo
                {
                    RenderPass = _renderPass,
                    AttachmentCount = attachments.Length,
                    Attachments = attachments.AsPointer(),
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                },
                null
            );
        }

        CreatePipeline();
    }

    // One depth buffer per swapchain image, so in-flight frames never share a depth attachment.
    private void CreateDepthResources(int index)
    {
        var image = _device.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = _depthFormat,
                Extent = new VkExtent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.DepthStencilAttachmentBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            null
        );

        var requirements = _device.GetImageMemoryRequirements(image);
        var memoryType = _physical.FindMemoryType((int)requirements.MemoryTypeBits, VkMemoryPropertyFlags.DeviceLocalBit);
        var memory = _device.AllocateMemory(
            new VkMemoryAllocateInfo { AllocationSize = requirements.Size, MemoryTypeIndex = memoryType },
            null
        );
        _device.BindImageMemory(image, memory, ByteSize.Zero);

        var view = _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = _depthFormat,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.DepthBit,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            },
            null
        );

        _depthImages[index] = image;
        _depthMemories[index] = memory;
        _depthViews[index] = view;
    }

    private VkFormat FindDepthFormat()
    {
        return _physical.FindSupportedFormat(
            [VkFormat.D32SFloat, VkFormat.D24UNormS8UInt, VkFormat.D16UNorm],
            VkImageTiling.Optimal,
            VkFormatFeatureFlags.DepthStencilAttachmentBit
        );
    }

    private VkRenderPass CreateRenderPass()
    {
        var attachment = new VkAttachmentDescription
        {
            Format = _format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.PresentSrcKHR,
        };

        var colorReference = new VkAttachmentReference
        {
            Attachment = 0,
            Layout = VkImageLayout.ColorAttachmentOptimal,
        };

        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorReference,
        };

        var depthAttachment = new VkAttachmentDescription
        {
            Format = _depthFormat,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
        };

        var depthReference = new VkAttachmentReference
        {
            Attachment = 1,
            Layout = VkImageLayout.DepthStencilAttachmentOptimal,
        };
        subpass.DepthStencilAttachment = &depthReference;

        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
        };

        Span<VkAttachmentDescription> attachments = [attachment, depthAttachment];
        return _device.CreateRenderPass(
            new VkRenderPassCreateInfo
            {
                AttachmentCount = attachments.Length,
                Attachments = attachments.AsPointer(),
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 1,
                Dependencies = &dependency,
            },
            null
        );
    }

    private void CreatePipeline()
    {
        var vertexShader = CompileShader(Shaders.Vertex, ShaderKind.VertexShader, "mesh.vert");
        var fragmentShader = CompileShader(Shaders.Fragment, ShaderKind.FragmentShader, "mesh.frag");

        try
        {
            var binding = new VkDescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
                ImmutableSamplers = null
            };

            _descriptorSetLayout = _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo
            {
                BindingCount = 1,
                Bindings = &binding
            }, null);

            var pushConstantRange = new VkPushConstantRange
            {
                StageFlags = VkShaderStageFlags.VertexBit,
                Offset = ByteSize.Zero,
                Size = ByteSize.Of(PushConstantSize),
            };

            // === ИСПРАВЛЕНИЕ: Оставляем одну строку сборки Layout, удалив затирающий дубликат ===
            VkDescriptorSetLayout layoutHandle = _descriptorSetLayout;
            _pipelineLayout = _device.CreatePipelineLayout([layoutHandle], [pushConstantRange], null);

            Span<VkPipelineShaderStageCreateInfo> stages =
            [
                Presets.ShaderStage.Vertex(vertexShader),
                Presets.ShaderStage.Fragment(fragmentShader),
            ];

            var bindingDescription = new VkVertexInputBindingDescription
            {
                Binding = 0,
                Stride = ByteSize.Of<Vertex>(),
                InputRate = VkVertexInputRate.Vertex,
            };

            Span<VkVertexInputAttributeDescription> attributes =
            [
                new() { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.OffsetOf<Vertex>(nameof(Vertex.Position)) },
                new() { Location = 1, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.OffsetOf<Vertex>(nameof(Vertex.Normal)) },
                new() { Location = 2, Binding = 0, Format = VkFormat.R32G32SFloat, Offset = ByteSize.OffsetOf<Vertex>(nameof(Vertex.Uv)) }
            ];

            var vertexInput = new VkPipelineVertexInputStateCreateInfo
            {
                VertexBindingDescriptionCount = 1,
                VertexBindingDescriptions = &bindingDescription,
                VertexAttributeDescriptionCount = attributes.Length,
                VertexAttributeDescriptions = attributes.AsPointer(),
            };

            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
            var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var rasterization = new VkPipelineRasterizationStateCreateInfo
            {
                PolygonMode = VkPolygonMode.Fill,
                CullMode = VkCullModeFlags.BackBit, // Отсекать задние грани
                FrontFace = VkFrontFace.CounterClockwise,
                LineWidth = 1.0f
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var depthStencil = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = VkCompareOp.LessOrEqual, MinDepthBounds = 0f, MaxDepthBounds = 1f };
            var colorAttachment = new VkPipelineColorBlendAttachmentState { BlendEnable = false, ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit };
            var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &colorAttachment };
            Span<VkDynamicState> dynamicStates = [VkDynamicState.Viewport, VkDynamicState.Scissor];
            var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = dynamicStates.Length, DynamicStates = dynamicStates.AsPointer() };

            _textureSampler = _device.CreateSampler(new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                AddressModeU = VkSamplerAddressMode.Repeat,
                AddressModeV = VkSamplerAddressMode.Repeat,
                AddressModeW = VkSamplerAddressMode.Repeat,
                MipmapMode = VkSamplerMipmapMode.Linear,
                MaxLod = 1.0f
            }, null);

            var poolSize = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 64 };

            _meshDescriptorPool = _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
            {
                MaxSets = 64, // <-- Позволяет выделить до 64 уникальных текстурных дескрипторов
                PoolSizeCount = 1,
                PoolSizes = &poolSize
            }, null);

            // Подготавливаем параметры аллокации
            var allocInfo = new VkDescriptorSetAllocateInfo
            {
                DescriptorPool = _meshDescriptorPool,
                DescriptorSetCount = 1,
                SetLayouts = &layoutHandle
            };

            // === ИСПРАВЛЕНИЕ ОШИБКИ CS1503 ===
            // Вместо небезопасного fixed-указателя, мы передаем Span-буфер из одного элемента.
            // Обертка запишет выделенный дескриптор прямо в локальную переменную, которую мы присвоим полю класса.
            VkDescriptorSet allocatedSet = default;
            _device.AllocateDescriptorSets(allocInfo, [allocatedSet]);
            _descriptorSet = allocatedSet;
            _pipeline = _device.CreateGraphicsPipeline(
                default,
                new VkGraphicsPipelineCreateInfo
                {
                    Flags = VkPipelineCreateFlags.None,
                    StageCount = stages.Length,
                    Stages = stages.AsPointer(),
                    VertexInputState = &vertexInput,
                    InputAssemblyState = &inputAssembly,
                    ViewportState = &viewportState,
                    RasterizationState = &rasterization,
                    MultisampleState = &multisample,
                    DepthStencilState = &depthStencil,
                    ColorBlendState = &colorBlend,
                    DynamicState = &dynamicState,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                },
                null
            );
        }
        finally
        {
            _device.DestroyShaderModule(vertexShader, null);
            _device.DestroyShaderModule(fragmentShader, null);
        }
    }


    private VkShaderModule CompileShader(string source, ShaderKind kind, string name)
    {
        var compiler = ShaderC.CreateCompiler();
        using var result = compiler.Compile(source, kind, name);

        var length = (int)(ByteSize)result.GetLength();
        var bytes = new ReadOnlySpan<byte>(result.GetBytes(), length);
        return ShaderModuleExtensions.FromSpirv(_device, bytes, null);
    }

    private (VkBuffer, VkDeviceMemory) CreateHostVisibleBuffer<T>(T[] data, VkBufferUsageFlags usage)
        where T : unmanaged
    {
        var size = ByteSize.Of<T>(data.Length);
        var buffer = _device.CreateBuffer(
            new VkBufferCreateInfo
            {
                Size = size,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
            },
            null
        );

        var requirements = _device.GetBufferMemoryRequirements(buffer);
        var memoryType = _physical.FindMemoryType(
            (int)requirements.MemoryTypeBits,
            VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit
        );

        var memory = _device.AllocateMemory(
            new VkMemoryAllocateInfo { AllocationSize = requirements.Size, MemoryTypeIndex = memoryType },
            null
        );
        _device.BindBufferMemory(buffer, memory, ByteSize.Zero);

        // Host-visible + coherent, so a single write is immediately visible to the GPU.
        _device.WriteMemory(data.AsSpan(), memory);

        return (buffer, memory);
    }

    private VkSurfaceFormatKHR SelectSurfaceFormat()
    {
        var formatCount = _physical.GetSurfaceFormatsKHR_Count(_surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[formatCount];
        _physical.GetSurfaceFormatsKHR_Write(_surface, formats);

        if (formats.Length == 1 && formats[0].Format == VkFormat.Undefined)
            return new VkSurfaceFormatKHR { Format = VkFormat.B8G8R8A8UNorm, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR };

        foreach (var format in formats)
            if (format.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm)
                return format;

        return formats[0];
    }

    private VkPresentModeKHR SelectPresentMode()
    {
        var modeCount = _physical.GetSurfacePresentModesKHR_Count(_surface);
        Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[modeCount];
        _physical.GetSurfacePresentModesKHR_Write(_surface, modes);

        foreach (var mode in modes)
            if (mode == VkPresentModeKHR.MailboxKHR)
                return VkPresentModeKHR.MailboxKHR;

        return VkPresentModeKHR.FifoKHR; // always available
    }

    private void RecreateSwapchain()
    {
        var framebufferSize = _window.FramebufferSize;
        if (framebufferSize.X == 0 || framebufferSize.Y == 0)
            return; // minimised; try again once it has a size

        _device.WaitIdle();

        DestroyPipelineResources();
        DestroySwapchainResources();

        CreateSwapchain();
        CreatePipelineResources();
    }

    private void DestroySwapchainResources()
    {
        foreach (var framebuffer in _framebuffers)
            _device.DestroyFramebuffer(framebuffer, null);
        _framebuffers = [];

        foreach (var imageView in _imageViews)
            _device.DestroyImageView(imageView, null);
        _imageViews = [];

        for (var i = 0; i < _depthViews.Length; i++)
        {
            _device.DestroyImageView(_depthViews[i], null);
            _device.DestroyImage(_depthImages[i], null);
            _device.FreeMemory(_depthMemories[i], null);
        }
        _depthViews = [];
        _depthImages = [];
        _depthMemories = [];

        if (!_swapchain.IsNull())
            _device.DestroySwapchainKHR(_swapchain, null);
        _swapchain = default;
        _swapchainImages = [];
    }

    private void DestroyPipelineResources()
    {
        if (!_descriptorSetLayout.IsNull())
            _device.DestroyDescriptorSetLayout(_descriptorSetLayout, null);
        if (!_textureSampler.IsNull())
            _device.DestroySampler(_textureSampler, null);
        _textureSampler = default;

        if (!_meshDescriptorPool.IsNull())
            _device.DestroyDescriptorPool(_meshDescriptorPool, null);
        _meshDescriptorPool = default;

        if (!_meshTexture.View.IsNull())
            _device.DestroyImageView(_meshTexture.View, null);
        if (!_meshTexture.Image.IsNull())
            _device.DestroyImage(_meshTexture.Image, null);
        if (!_meshTexture.Memory.IsNull())
            _device.FreeMemory(_meshTexture.Memory, null);
        _meshTexture = default;
        _descriptorSet = default;

        if (!_pipeline.IsNull())
            _device.DestroyPipeline(_pipeline, null);
        _pipeline = default;

        if (!_pipelineLayout.IsNull())
            _device.DestroyPipelineLayout(_pipelineLayout, null);
        _pipelineLayout = default;

        if (!_renderPass.IsNull())
            _device.DestroyRenderPass(_renderPass, null);
        _renderPass = default;
    }

    private void DestroyMesh()
    {
        // Очистка глобального отладочного буфера меша
        if (!_vertexBuffer.IsNull()) { _device.DestroyBuffer(_vertexBuffer, null); _vertexBuffer = default; }
        if (!_vertexMemory.IsNull()) { _device.FreeMemory(_vertexMemory, null); _vertexMemory = default; }
        if (!_indexBuffer.IsNull()) { _device.DestroyBuffer(_indexBuffer, null); _indexBuffer = default; }
        if (!_indexMemory.IsNull()) { _device.FreeMemory(_indexMemory, null); _indexMemory = default; }

        // Очистка динамически выделенной памяти для всех мешей из Dictionary
        foreach (var tex in _vulkanAllocatedTextures.Values)
        {
            // Если вы решите хранить буферы мешей в отдельном списке, 
            // не забывайте вызывать для них аналогичный DestroyBuffer и FreeMemory
        }

        _vertexCount = 0;
        _indexCount = 0;
    }


    public void Dispose()
    {
        _device.WaitIdle();

        DestroyMesh();
        DestroyPipelineResources();
        DestroySwapchainResources();

        foreach (var frame in _frames)
        {
            _device.DestroyFence(frame.Fence, null);
            _device.DestroySemaphore(frame.ImageAvailable, null);
            _device.DestroySemaphore(frame.RenderFinished, null);
            _device.DestroyCommandPool(frame.Pool, null);
        }

        _device.Dispose();
        _instance.DestroySurfaceKHR(_surface, null);
        _instance.Dispose();
    }
}
