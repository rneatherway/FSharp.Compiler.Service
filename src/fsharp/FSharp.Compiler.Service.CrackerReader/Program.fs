// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.
open System.IO
open System
type CrackerProjectOptions =
  {
    ProjectFile: string
    Options: string[]
    ReferencedProjectOptions: (string * CrackerProjectOptions)[]
    LogOutput: string
  }
[<EntryPoint>]
let main argv = 


    let p = new System.Diagnostics.Process()
    p.StartInfo.FileName <- Path.Combine(Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location),
                                          "FSharp.Compiler.Service.ProjectCracker.exe")
    p.StartInfo.Arguments <- @"d:\dev\FSharp.Compiler.Service\src\fsharp\FSharp.Compiler.Service.CrackerReader\FSharp.Compiler.Service.CrackerReader.fsproj false"
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    ignore <| p.Start()
    //let s = p.StandardOutput.ReadToEnd()
    //printfn "%s" s
    
    //let stream = new MemoryStream()
    //let writer = new StreamWriter(stream)
    //writer.Write(s)
    //writer.Flush()
    //stream.Position <- 0L

    //let fmt = new Serialization.Formatters.Binary.BinaryFormatter()
    let ser = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof<CrackerProjectOptions>)
    let opts = ser.ReadObject(p.StandardOutput.BaseStream) :?> CrackerProjectOptions
    //let opts = fmt.Deserialize(p.StandardOutput.BaseStream) :?> FSharp.Compiler.Service.ProjectCracker.ProjectOptions
    p.WaitForExit()
        
    printfn "%A" opts
    0