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
    uniform mat4 normalMatrix;
    
    out vec3 vNormal;
    out vec2 vTexCoord;
    out vec3 vFragPos;
    out float vVisibility; 
    
    void main()
    {
        gl_Position = mvp * vec4(inPosition, 1.0);
        
        vNormal = normalize(mat3(normalMatrix) * inNormal);
        vFragPos = vec3(model * vec4(inPosition, 1.0));
        vTexCoord = inTexCoord;
        
        // Передаем глубину (расстояние до вершины в пространстве отсечения)
        vVisibility = gl_Position.z;
    }
    """;

    public const string OpenGlFragment = """
    #version 330 core
    
    in vec3 vNormal;
    in vec2 vTexCoord;
    in vec3 vFragPos;
    in float vVisibility;
    
    uniform sampler2D uGameTexture;
    uniform vec3 uViewPos;
    
    out vec4 outColor;
    
    // Ослабленный туман (границы отодвинуты дальше)
    const float fogNear = 250.0;
    const float fogFar = 450.0;
    const vec3 fogColor = vec3(0.53, 0.81, 0.92); // Sky Blue
    
    void main()
    {
        vec3 n = normalize(vNormal);
        vec3 lightDir = normalize(vec3(0.35, 0.75, 0.55));
        
        float ambientStrength = 0.35;
        vec3 ambient = ambientStrength * fogColor;
        
        float lambert = max(dot(n, lightDir), 0.0);
        vec3 diffuse = lambert * vec3(1.0);
        
        vec3 viewDir = normalize(uViewPos - vFragPos);
        vec3 halfDir = normalize(lightDir + viewDir);
        float spec = pow(max(dot(n, halfDir), 0.0), 32.0);
        vec3 specular = 0.2 * spec * vec3(1.0);
        
        vec4 texColor = texture(uGameTexture, vTexCoord);
        vec3 finalObjColor = (ambient + diffuse + specular) * texColor.rgb;
        
        float fogFactor = clamp((vVisibility - fogNear) / (fogFar - fogNear), 0.0, 1.0);
        vec3 finalColorWithFog = mix(finalObjColor, fogColor, fogFactor);
        
        outColor = vec4(finalColorWithFog, 1.0);
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
    layout(location = 3) out float vVisibility; // Передаем фактор для Vulkan

    void main()
    {
        vec4 clip = pc.mvp * vec4(inPosition, 1.0);
        clip.y = -clip.y;
        clip.z = (clip.z + clip.w) * 0.5;
        gl_Position = clip;
        
        vNormal = normalize(mat3(pc.normalMatrix) * inNormal);
        vFragPos = vec3(pc.model * vec4(inPosition, 1.0));
        vTexCoord = inTexCoord;
        
        // Для Vulkan глубина берется из координаты z после трансформации
        vVisibility = gl_Position.z;
    }
    """;

    public const string Fragment = """
    #version 450

    layout(location = 0) in vec3 vNormal;
    layout(location = 1) in vec2 vTexCoord; 
    layout(location = 2) in vec3 vFragPos;
    layout(location = 3) in float vVisibility;

    layout(binding = 0) uniform sampler2D uGameTexture;

    const vec3 viewPos = vec3(8.0, 7.0, 12.0); 
    
    const float fogNear = 250.0;
    const float fogFar = 450.0;
    const vec3 fogColor = vec3(0.53, 0.81, 0.92);

    layout(location = 0) out vec4 outColor;

    void main()
    {
        vec3 n = normalize(vNormal);
        vec3 lightDir = normalize(vec3(0.35, 0.75, 0.55));
        
        float ambientStrength = 0.35;
        vec3 ambient = ambientStrength * fogColor;
        
        float lambert = max(dot(n, lightDir), 0.0);
        vec3 diffuse = lambert * vec3(1.0);
        
        vec3 viewDir = normalize(viewPos - vFragPos);
        vec3 halfDir = normalize(lightDir + viewDir);
        float spec = pow(max(dot(n, halfDir), 0.0), 32.0);
        vec3 specular = 0.4 * spec * vec3(1.0);
        
        vec4 texColor = texture(uGameTexture, vTexCoord);
        vec3 finalObjColor = texColor.rgb * (ambient + diffuse + specular);
        
        float fogFactor = clamp((vVisibility - fogNear) / (fogFar - fogNear), 0.0, 1.0);
        vec3 finalColorWithFog = mix(finalObjColor, fogColor, fogFactor);
        
        outColor = vec4(finalColorWithFog, 1.0);
    }
    """;
}
