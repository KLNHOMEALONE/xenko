// <auto-generated>
// Do not edit this file yourself!
//
// This code was generated by Paradox Shader Mixin Code Generator.
// To generate it yourself, please install SiliconStudio.Paradox.VisualStudio.Package .vsix
// and re-save the associated .pdxfx.
// </auto-generated>

using SiliconStudio.Core;
using SiliconStudio.Paradox.Effects;
using SiliconStudio.Paradox.Shaders;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Paradox.Graphics;


#line 3 "C:\Projects\Paradox\sources\shaders\BasicShaders.pdxfx"
using SiliconStudio.Paradox.Effects.Data;

#line 4
using SiliconStudio.Paradox.Effects.Modules;

#line 6
namespace BasicShaders
{
    [DataContract]
#line 8
    public partial class BasicShadersParameters : ShaderMixinParameters
    {

        #line 10
        public static readonly ParameterKey<bool> UsedNormalMap = ParameterKeys.New<bool>(false);
    };

    #line 16
    public partial class ParadoxTessellation  : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSourceTree mixin, ShaderMixinContext context)
        {
        }

        [ModuleInitializer]
        internal static void __Initialize__()

        {
            ShaderMixinManager.Register("ParadoxTessellation", new ParadoxTessellation());
        }
    }

    #line 30
    public partial class ParadoxSkinning  : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSourceTree mixin, ShaderMixinContext context)
        {

            #line 35
            if (context.GetParam(MaterialParameters.HasSkinningPosition))
            {

                #line 37
                if (context.GetParam(MaterialParameters.SkinningBones) > context.GetParam(MaterialParameters.SkinningMaxBones))
                {

                    #line 40
                    context.SetParam(MaterialParameters.SkinningMaxBones, context.GetParam(MaterialParameters.SkinningBones));
                }

                #line 42
                mixin.Mixin.AddMacro("SkinningMaxBones", context.GetParam(MaterialParameters.SkinningMaxBones));

                #line 43
                context.Mixin(mixin, "TransformationSkinning");

                #line 45
                if (context.GetParam(MaterialParameters.HasSkinningNormal))
                {

                    #line 47
                    if (context.GetParam(BasicShadersParameters.UsedNormalMap))

                        #line 48
                        context.Mixin(mixin, "TangentToViewSkinning");

                    #line 50
                    else

                        #line 50
                        context.Mixin(mixin, "NormalVSSkinning");

                    #line 52
                    context.Mixin(mixin, "NormalSkinning");
                }

                #line 55
                if (context.GetParam(MaterialParameters.HasSkinningTangent))

                    #line 56
                    context.Mixin(mixin, "TangentSkinning");
            }
        }

        [ModuleInitializer]
        internal static void __Initialize__()

        {
            ShaderMixinManager.Register("ParadoxSkinning", new ParadoxSkinning());
        }
    }

    #line 63
    public partial class ParadoxShadowCast  : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSourceTree mixin, ShaderMixinContext context)
        {

            #line 68
            if (context.GetParam(LightingKeys.CastShadows))

                {

                    #line 69
                    var __subMixin = new ShaderMixinSourceTree() { Name = "ShadowMapCaster", Parent = mixin };
                    mixin.Children.Add(__subMixin);

                    #line 69
                    context.BeginChild(__subMixin);

                    #line 69
                    context.Mixin(__subMixin, "ShadowMapCaster");

                    #line 69
                    context.EndChild();
                }
        }

        [ModuleInitializer]
        internal static void __Initialize__()

        {
            ShaderMixinManager.Register("ParadoxShadowCast", new ParadoxShadowCast());
        }
    }

    #line 75
    public partial class ParadoxBaseShader  : IShaderMixinBuilder
    {
        public void Generate(ShaderMixinSourceTree mixin, ShaderMixinContext context)
        {

            #line 80
            context.Mixin(mixin, "ShaderBase");

            #line 81
            context.Mixin(mixin, "ShadingBase");

            #line 82
            context.Mixin(mixin, "TransformationWAndVP");

            #line 84
            context.Mixin(mixin, "PositionVSStream");

            #line 86
            if (context.GetParam(MaterialParameters.NormalMap) != null)
            {

                #line 88
                context.Mixin(mixin, "NormalMapTexture");

                {

                    #line 89
                    var __subMixin = new ShaderMixinSourceTree() { Parent = mixin };

                    #line 89
                    context.Mixin(__subMixin, context.GetParam(MaterialParameters.NormalMap));
                    mixin.Mixin.AddComposition("normalMap", __subMixin.Mixin);
                }

                #line 90
                context.SetParam(BasicShadersParameters.UsedNormalMap, true);
            }

            #line 93
            else
            {

                #line 94
                context.Mixin(mixin, "NormalVSStream");
            }
        }

        [ModuleInitializer]
        internal static void __Initialize__()

        {
            ShaderMixinManager.Register("ParadoxBaseShader", new ParadoxBaseShader());
        }
    }
}
