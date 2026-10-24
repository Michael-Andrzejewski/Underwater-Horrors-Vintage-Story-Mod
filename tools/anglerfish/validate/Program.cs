using System.Reflection;
using System.Runtime.Loader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

string repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string game = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vintagestory");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string folder in new[] { game, Path.Combine(game, "Lib") })
    {
        string file = Path.Combine(folder, name.Name + ".dll");
        if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    }
    return null;
};
Validate();
CombatChecks.Run();

void Validate()
{
    string file = Path.Combine(repo,"assets/underwaterhorrors/shapes/entity/anglerfish.json");
    var shape = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(file));
    var raw = JObject.Parse(File.ReadAllText(file));
    var names = new HashSet<string>();
    int faces = 0;
    void Check(JToken element)
    {
        string name = element["name"].Value<string>();
        if (!names.Add(name)) throw new Exception("Duplicate element " + name);
        foreach (var property in (element["faces"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            faces++;
            if (property.Value["texture"].Value<string>() != "#anglerfish") throw new Exception("Unresolved texture");
            foreach (var uv in property.Value["uv"]) if (uv.Value<double>() < 0 || uv.Value<double>() > 1024) throw new Exception("UV outside atlas");
        }
        foreach(var c in element["children"] ?? new JArray()) Check(c);
    }
    foreach(var e in raw["elements"]) Check(e);
    foreach(var a in raw["animations"])
    {
        int previous = -1;
        foreach(var k in a["keyframes"])
        {
            int f=k["frame"].Value<int>();
            if (f <= previous || f >= a["quantityframes"].Value<int>()) throw new Exception("Invalid frame sequence");
            previous=f;
            foreach(var p in ((JObject)k["elements"]).Properties()) if(!names.Contains(p.Name)) throw new Exception("Unresolved animation joint "+p.Name);
        }
        if(a["onAnimationEnd"].Value<string>()=="Repeat")
        {
            var first=(JObject)a["keyframes"].First["elements"];
            var last=(JObject)a["keyframes"].Last["elements"];
            foreach(var p in first.Properties()) foreach(var axis in ((JObject)p.Value).Properties())
                if(Math.Abs(axis.Value.Value<double>() - last[p.Name][axis.Name].Value<double>())>0.00001) throw new Exception("Discontinuous loop "+a["code"]);
        }
    }
    shape.InitForAnimations(null,"anglerfish");
    foreach(var animation in shape.Animations)
    {
        animation.GenerateAllFrames(shape.Elements,shape.JointsById);
        if(animation.PrevNextKeyFrameByFrame.Length != animation.QuantityFrames) throw new Exception("Incomplete native animation");
        Console.WriteLine($"PASS native animation {animation.Code}: {animation.QuantityFrames} frames");
    }
    var minimum=new double[]{double.MaxValue,double.MaxValue,double.MaxValue};
    var maximum=new double[]{double.MinValue,double.MinValue,double.MinValue};
    void Bounds(ShapeElement e,float[] parent)
    {
        var matrix=Mat4f.Mul(Mat4f.Create(),parent,e.GetLocalTransformMatrix(0));
        if(e.FacesResolved.Any(f=>f!=null))for(int corner=0;corner<8;corner++)
        {
            var p=new double[]{(corner&1)==0?0:(e.To[0]-e.From[0])/16,(corner&2)==0?0:(e.To[1]-e.From[1])/16,(corner&4)==0?0:(e.To[2]-e.From[2])/16};
            for(int axis=0;axis<3;axis++)
            {
                double v=matrix[axis]*p[0]+matrix[4+axis]*p[1]+matrix[8+axis]*p[2]+matrix[12+axis];
                minimum[axis]=Math.Min(minimum[axis],v);maximum[axis]=Math.Max(maximum[axis],v);
            }
        }
        foreach(var c in e.Children??Array.Empty<ShapeElement>())Bounds(c,matrix);
    }
    foreach(var e in shape.Elements)Bounds(e,Mat4f.Create());
    Console.WriteLine($"PASS {names.Count} unique elements, {faces} mapped faces, {shape.JointsById.Count} native joints");
    Console.WriteLine("Rest bounds at entity size 2, blocks (X length, Y height, Z width): "+string.Join(", ",minimum.Select((v,i)=>((maximum[i]-v)*2).ToString("F2"))));
    string boatPath=Path.Combine(game,"assets/survival/shapes/entity/nonliving/boat/sailed.json");
    if(File.Exists(boatPath))
    {
        var boat=JsonConvert.DeserializeObject<Shape>(File.ReadAllText(boatPath));
        Array.Fill(minimum,double.MaxValue);Array.Fill(maximum,double.MinValue);
        foreach(var e in boat.Elements)Bounds(e,Mat4f.Create());
        Console.WriteLine("Installed sailboat shape rest bounds, blocks: "+string.Join(", ",minimum.Select((v,i)=>(maximum[i]-v).ToString("F2"))));
    }
    foreach(string code in new[]{"anglerfish-live","anglerfishlure","anglerfishluresegment"})
    {
        var runtime=JsonConvert.DeserializeObject<Shape>(File.ReadAllText(Path.Combine(repo,"assets/underwaterhorrors/shapes/entity/"+code+".json")));
        runtime.InitForAnimations(null,code);
        foreach(var a in runtime.Animations??Array.Empty<Animation>())a.GenerateAllFrames(runtime.Elements,runtime.JointsById);
        Console.WriteLine($"PASS native runtime shape {code}: {runtime.JointsById.Count} joints");
    }
}
