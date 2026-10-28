namespace BrutalSandbox;

internal static class Shaders
{
    public const string OpenGlVertex = """
    #version 330 core
    
    layout(location = 0) in vec3 inPosition;
    layout(location = 1) in vec3 inNormal;
    layout(location = 2) in vec2 inTexCoord;
    
    uniform mat4 mvp;
    uniform mat4 model;
    uniform mat4 normalMatrix; // Матрица для правильной трансформации нормалей
    
    out vec3 vNormal;
    out vec2 vTexCoord;
    out vec3 vFragPos; // Позиция фрагмента в мировом пространстве для бликов
    
    void main()
    {
        gl_Position = mvp * vec4(inPosition, 1.0);
        
        // Умножаем на матрицу нормалей для корректного вращения без искажения масштабом
        vNormal = normalize(mat3(normalMatrix) * inNormal);
        
        vFragPos = vec3(model * vec4(inPosition, 1.0));
        vTexCoord = inTexCoord;
    }
    """;

    public const string OpenGlFragment = """
    #version 330 core
    
    in vec3 vNormal;
    in vec2 vTexCoord;
    in vec3 vFragPos;
    
    uniform sampler2D uGameTexture;
    uniform vec3 uViewPos; // Позиция камеры в мире для расчета бликов
    
    out vec4 outColor;
    
    void main()
    {
        vec3 n = normalize(vNormal);
        vec3 lightDir = normalize(vec3(0.35, 0.75, 0.55)); // Направление света
        
        // 1. Ambient (Фоновое освещение)
        float ambientStrength = 0.25;
        vec3 ambient = ambientStrength * vec3(1.0);
        
        // 2. Diffuse (Диффузное освещение Ламберта)
        float lambert = max(dot(n, lightDir), 0.0);
        vec3 diffuse = lambert * vec3(1.0);
        
        // 3. Specular (Зеркальные блики по Блинну-Фонгу)
        vec3 viewDir = normalize(uViewPos - vFragPos);
        vec3 halfDir = normalize(lightDir + viewDir); // Полувектор
        float spec = pow(max(dot(n, halfDir), 0.0), 32.0); // 32.0 — жесткость блика
        vec3 specular = 0.4 * spec * vec3(1.0); // 0.4 — интенсивность блика
        
        // Выборка из текстуры
        vec4 texColor = texture(uGameTexture, vTexCoord);
        
        // Итоговый цвет с учетом всех трех компонент
        vec3 lighting = (ambient + diffuse + specular) * texColor.rgb;
        outColor = vec4(lighting, 1.0);
    }
    """;

    public const string Vertex = """
    #version 450

    layout(location = 0) in vec3 inPosition;
    layout(location = 1) in vec3 inNormal;
    layout(location = 2) in vec2 inTexCoord; 

    layout(push_constant) uniform PushConstants
    {
        mat4 mvp;
        mat4 model;
        mat4 normalMatrix;
    } pc;

    layout(location = 0) out vec3 vNormal;
    layout(location = 1) out vec2 vTexCoord;  
    layout(location = 2) out vec3 vFragPos;

    void main()
    {
        vec4 clip = pc.mvp * vec4(inPosition, 1.0);
        clip.y = -clip.y;
        clip.z = (clip.z + clip.w) * 0.5;
        gl_Position = clip;
        
        vNormal = normalize(mat3(pc.normalMatrix) * inNormal);
        vFragPos = vec3(pc.model * vec4(inPosition, 1.0));
        vTexCoord = inTexCoord;
    }
    """;

    public const string Fragment = """
    #version 450

    layout(location = 0) in vec3 vNormal;
    layout(location = 1) in vec2 vTexCoord; 
    layout(location = 2) in vec3 vFragPos;

    layout(binding = 0) uniform sampler2D uGameTexture;

    // В Vulkan для простоты передаем позицию камеры константой (или через push-constants во фрагментный)
    // Напрямую зашьем среднюю позицию Orbit-камеры для теста, либо расширим push-constants
    const vec3 viewPos = vec3(8.0, 7.0, 12.0); 

    layout(location = 0) out vec4 outColor;

    void main()
    {
        vec3 n = normalize(vNormal);
        vec3 lightDir = normalize(vec3(0.35, 0.75, 0.55));
        
        float ambientStrength = 0.25;
        vec3 ambient = ambientStrength * vec3(1.0);
        
        float lambert = max(dot(n, lightDir), 0.0);
        vec3 diffuse = lambert * vec3(1.0);
        
        vec3 viewDir = normalize(viewPos - vFragPos);
        vec3 halfDir = normalize(lightDir + viewDir);
        float spec = pow(max(dot(n, halfDir), 0.0), 32.0);
        vec3 specular = 0.4 * spec * vec3(1.0);
        
        vec4 texColor = texture(uGameTexture, vTexCoord);
        
        outColor = vec4(texColor.rgb * (ambient + diffuse + specular), 1.0);
    }
    """;
}
