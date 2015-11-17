﻿namespace FSharp.Compiler.Service.ProjectCracker

open Microsoft.Build.Framework
open Microsoft.Build.Utilities
open System.Text
open System.IO
open System
open System.Reflection
open System.Runtime.Serialization.Formatters.Binary
open System.Runtime.Serialization.Json

module Program =
  let runningOnMono = 
      try match System.Type.GetType("Mono.Runtime") with null -> false | _ -> true
      with e -> false

  type internal BasicStringLogger() =
    inherit Logger()

    let sb = new StringBuilder()

    let log (e: BuildEventArgs) =
      sb.Append(e.Message) |> ignore
      sb.AppendLine() |> ignore

    override x.Initialize(eventSource:IEventSource) =
      sb.Clear() |> ignore
      eventSource.AnyEventRaised.Add(log)
    
    member x.Log = sb.ToString()

  type internal HostCompile() =
      member th.Compile(_, _, _) = 0
      interface ITaskHost

  //----------------------------------------------------------------------------
  // FSharpProjectFileInfo
  //
  [<Sealed; AutoSerializable(false)>]
  type FSharpProjectFileInfo (fsprojFileName:string, ?properties, ?enableLogging) =

      let properties = defaultArg properties []
      let enableLogging = defaultArg enableLogging false
      let mkAbsolute dir v = 
          if Path.IsPathRooted v then v
          else Path.Combine(dir, v)

      let mkAbsoluteOpt dir v =  Option.map (mkAbsolute dir) v

      let logOpt =
          if enableLogging then
              let log = new BasicStringLogger()
              do log.Verbosity <- Microsoft.Build.Framework.LoggerVerbosity.Diagnostic
              Some log
          else
              None

      // Use the old API on Mono, with ToolsVersion = 12.0
      let CrackProjectUsingOldBuildAPI(fsprojFile:string) = 
          let engine = new Microsoft.Build.BuildEngine.Engine()
          try
             engine.DefaultToolsVersion <- "12.0"
          with | _ -> engine.DefaultToolsVersion <- "4.0"

          Option.iter (fun l -> engine.RegisterLogger(l)) logOpt

          let bpg = Microsoft.Build.BuildEngine.BuildPropertyGroup()

          bpg.SetProperty("BuildingInsideVisualStudio", "true")
          for (prop, value) in properties do
              bpg.SetProperty(prop, value)

          engine.GlobalProperties <- bpg

          let projectFromFile (fsprojFile:string) =
              // We seem to need to pass 12.0/4.0 in here for some unknown reason
              let project = new Microsoft.Build.BuildEngine.Project(engine, engine.DefaultToolsVersion)
              do project.Load(fsprojFile)
              project

          let project = projectFromFile fsprojFile
          project.Build([| "ResolveReferences" |])  |> ignore
          let directory = Path.GetDirectoryName project.FullFileName

          let getProp (p: Microsoft.Build.BuildEngine.Project) s = 
              let v = p.GetEvaluatedProperty s
              if String.IsNullOrWhiteSpace v then None
              else Some v

          let outFileOpt =
              match mkAbsoluteOpt directory (getProp project "OutDir") with
              | None -> None
              | Some d -> mkAbsoluteOpt d (getProp project "TargetFileName")

          let getItems s = 
              let fs  = project.GetEvaluatedItemsByName(s)
              [ for f in fs -> mkAbsolute directory f.FinalItemSpec ]

          let projectReferences =
              [  for i in project.GetEvaluatedItemsByName("ProjectReference") do
                     yield mkAbsolute directory i.FinalItemSpec
              ]

          let references = 
              [ for i in project.GetEvaluatedItemsByName("ReferencePath") do
                  yield i.FinalItemSpec
                for i in project.GetEvaluatedItemsByName("ChildProjectReferences") do
                  yield i.FinalItemSpec ]
              // Duplicate slashes sometimes appear in the output here, which prevents
              // them from matching keys used in FSharpProjectOptions.ReferencedProjects
              |> List.map (fun (s: string) -> s.Replace("//","/"))

          outFileOpt, directory, getItems, references, projectReferences, getProp project, project.FullFileName

      let CrackProjectUsingNewBuildAPI(fsprojFile) =
          let fsprojFullPath = try Path.GetFullPath(fsprojFile) with _ -> fsprojFile
          let fsprojAbsDirectory = Path.GetDirectoryName fsprojFullPath

          use _pwd = 
              let dir = Environment.CurrentDirectory
              Environment.CurrentDirectory <- fsprojAbsDirectory
              { new System.IDisposable with member x.Dispose() = Environment.CurrentDirectory <- dir }
          use engine = new Microsoft.Build.Evaluation.ProjectCollection()
          let host = new HostCompile()
          engine.HostServices.RegisterHostObject(fsprojFullPath, "CoreCompile", "Fsc", host)

          let projectInstanceFromFullPath (fsprojFullPath: string) =
              use stream = new IO.StreamReader(fsprojFullPath)
              use xmlReader = System.Xml.XmlReader.Create(stream)

              let project = engine.LoadProject(xmlReader, FullPath=fsprojFullPath)

              project.SetGlobalProperty("BuildingInsideVisualStudio", "true") |> ignore
              project.SetGlobalProperty("VisualStudioVersion", "12.0") |> ignore
              for (prop, value) in properties do
                  project.SetProperty(prop, value) |> ignore

              project.CreateProjectInstance()

          let project = projectInstanceFromFullPath fsprojFullPath
          let directory = project.Directory

          let getprop (p: Microsoft.Build.Execution.ProjectInstance) s =
              let v = p.GetPropertyValue s
              if String.IsNullOrWhiteSpace v then None
              else Some v

          let outFileOpt = getprop project "TargetPath"

          let log = match logOpt with
                    | None -> []
                    | Some l -> [l :> ILogger]

          project.Build([| "Build" |], log) |> ignore

          let getItems s = [ for f in project.GetItems(s) -> mkAbsolute directory f.EvaluatedInclude ]

          let projectReferences =
                [ for cp in project.GetItems("ProjectReference") do
                      yield cp.GetMetadataValue("FullPath")
                ]

          let references =
                [ for i in project.GetItems("ReferencePath") do
                    yield i.EvaluatedInclude
                  for i in project.GetItems("ChildProjectReferences") do
                    yield i.EvaluatedInclude ]

          outFileOpt, directory, getItems, references, projectReferences, getprop project, project.FullPath

      let outFileOpt, directory, getItems, references, projectReferences, getProp, fsprojFullPath =
        try
          if runningOnMono then
              CrackProjectUsingOldBuildAPI(fsprojFileName)
          else
              CrackProjectUsingNewBuildAPI(fsprojFileName)
        with
          | :? Microsoft.Build.BuildEngine.InvalidProjectFileException as e ->
               raise (Microsoft.Build.Exceptions.InvalidProjectFileException(
                           e.ProjectFile,
                           e.LineNumber,
                           e.ColumnNumber,
                           e.EndLineNumber,
                           e.EndColumnNumber,
                           e.Message,
                           e.ErrorSubcategory,
                           e.ErrorCode,
                           e.HelpKeyword))
          | :? ArgumentException as e -> raise (IO.FileNotFoundException(e.Message))


      let logOutput = match logOpt with None -> "" | Some l -> l.Log
      let pages = getItems "Page"
      let embeddedResources = getItems "EmbeddedResource"
      let files = getItems "Compile"
      let resources = getItems "Resource"
      let noaction = getItems "None"
      let content = getItems "Content"
    
      let split (s : string option) (cs : char []) = 
          match s with
          | None -> [||]
          | Some s -> 
              if String.IsNullOrWhiteSpace s then [||]
              else s.Split(cs, StringSplitOptions.RemoveEmptyEntries)
    
      let getbool (s : string option) = 
          match s with
          | None -> false
          | Some s -> 
              match (Boolean.TryParse s) with
              | (true, result) -> result
              | (false, _) -> false
    
      let fxVer = getProp "TargetFrameworkVersion"
      let optimize = getProp "Optimize" |> getbool
      let assemblyNameOpt = getProp "AssemblyName"
      let tailcalls = getProp "Tailcalls" |> getbool
      let outputPathOpt = getProp "OutputPath"
      let docFileOpt = getProp "DocumentationFile"
      let outputTypeOpt = getProp "OutputType"
      let debugTypeOpt = getProp "DebugType"
      let baseAddressOpt = getProp "BaseAddress"
      let sigFileOpt = getProp "GenerateSignatureFile"
      let keyFileOpt = getProp "KeyFile"
      let pdbFileOpt = getProp "PdbFile"
      let platformOpt = getProp "Platform"
      let targetTypeOpt = getProp "TargetType"
      let versionFileOpt = getProp "VersionFile"
      let targetProfileOpt = getProp "TargetProfile"
      let warnLevelOpt = getProp "Warn"
      let subsystemVersionOpt = getProp "SubsystemVersion"
      let win32ResOpt = getProp "Win32ResourceFile"
      let heOpt = getProp "HighEntropyVA" |> getbool
      let win32ManifestOpt = getProp "Win32ManifestFile"
      let debugSymbols = getProp "DebugSymbols" |> getbool
      let prefer32bit = getProp "Prefer32Bit" |> getbool
      let warnAsError = getProp "TreatWarningsAsErrors" |> getbool
      let defines = split (getProp "DefineConstants") [| ';'; ','; ' ' |]
      let nowarn = split (getProp "NoWarn") [| ';'; ','; ' ' |]
      let warningsAsError = split (getProp "WarningsAsErrors") [| ';'; ','; ' ' |]
      let libPaths = split (getProp "ReferencePath") [| ';'; ',' |]
      let otherFlags = split (getProp "OtherFlags") [| ' ' |]
      let isLib = (outputTypeOpt = Some "Library")
    
      let docFileOpt = 
          match docFileOpt with
          | None -> None
          | Some docFile -> Some(mkAbsolute directory docFile)
    
    
      let options = 
          [   yield "--simpleresolution"
              yield "--noframework"
              match outFileOpt with
              | None -> ()
              | Some outFile -> yield "--out:" + outFile
              match docFileOpt with
              | None -> ()
              | Some docFile -> yield "--doc:" + docFile
              match baseAddressOpt with
              | None -> ()
              | Some baseAddress -> yield "--baseaddress:" + baseAddress
              match keyFileOpt with
              | None -> ()
              | Some keyFile -> yield "--keyfile:" + keyFile
              match sigFileOpt with
              | None -> ()
              | Some sigFile -> yield "--sig:" + sigFile
              match pdbFileOpt with
              | None -> ()
              | Some pdbFile -> yield "--pdb:" + pdbFile
              match versionFileOpt with
              | None -> ()
              | Some versionFile -> yield "--versionfile:" + versionFile
              match warnLevelOpt with
              | None -> ()
              | Some warnLevel -> yield "--warn:" + warnLevel
              match subsystemVersionOpt with
              | None -> ()
              | Some s -> yield "--subsystemversion:" + s
              if heOpt then yield "--highentropyva+"
              match win32ResOpt with
              | None -> ()
              | Some win32Res -> yield "--win32res:" + win32Res
              match win32ManifestOpt with
              | None -> ()
              | Some win32Manifest -> yield "--win32manifest:" + win32Manifest
              match targetProfileOpt with
              | None -> ()
              | Some targetProfile -> yield "--targetprofile:" + targetProfile
              yield "--fullpaths"
              yield "--flaterrors"
              if warnAsError then yield "--warnaserror"
              yield 
                  if isLib then "--target:library"
                  else "--target:exe"
              for symbol in defines do
                  if not (String.IsNullOrWhiteSpace symbol) then yield "--define:" + symbol
              for nw in nowarn do
                  if not (String.IsNullOrWhiteSpace nw) then yield "--nowarn:" + nw
              for nw in warningsAsError do
                  if not (String.IsNullOrWhiteSpace nw) then yield "--warnaserror:" + nw
              yield if debugSymbols then "--debug+"
                      else "--debug-"
              yield if optimize then "--optimize+"
                      else "--optimize-"
              yield if tailcalls then "--tailcalls+"
                      else "--tailcalls-"
              match debugTypeOpt with
              | None -> ()
              | Some debugType -> 
                  match debugType.ToUpperInvariant() with
                  | "NONE" -> ()
                  | "PDBONLY" -> yield "--debug:pdbonly"
                  | "FULL" -> yield "--debug:full"
                  | _ -> ()
              match platformOpt |> Option.map (fun o -> o.ToUpperInvariant()), prefer32bit, 
                      targetTypeOpt |> Option.map (fun o -> o.ToUpperInvariant()) with
              | Some "ANYCPU", true, Some "EXE" | Some "ANYCPU", true, Some "WINEXE" -> yield "--platform:anycpu32bitpreferred"
              | Some "ANYCPU", _, _ -> yield "--platform:anycpu"
              | Some "X86", _, _ -> yield "--platform:x86"
              | Some "X64", _, _ -> yield "--platform:x64"
              | Some "ITANIUM", _, _ -> yield "--platform:Itanium"
              | _ -> ()
              match targetTypeOpt |> Option.map (fun o -> o.ToUpperInvariant()) with
              | Some "LIBRARY" -> yield "--target:library"
              | Some "EXE" -> yield "--target:exe"
              | Some "WINEXE" -> yield "--target:winexe"
              | Some "MODULE" -> yield "--target:module"
              | _ -> ()
              yield! otherFlags
              for f in resources do
                  yield "--resource:" + f
              for i in libPaths do
                  yield "--lib:" + mkAbsolute directory i 
              for r in references do
                  yield "-r:" + r 
              yield! files ]
    
      member x.Options = options
      member x.FrameworkVersion = fxVer
      member x.ProjectReferences = projectReferences
      member x.References = references
      member x.CompileFiles = files
      member x.ResourceFiles = resources
      member x.EmbeddedResourceFiles = embeddedResources
      member x.ContentFiles = content
      member x.OtherFiles = noaction
      member x.PageFiles = pages
      member x.OutputFile = outFileOpt
      member x.Directory = directory
      member x.AssemblyName = assemblyNameOpt
      member x.OutputPath = outputPathOpt
      member x.FullPath = fsprojFullPath
      member x.LogOutput = logOutput
      static member Parse(fsprojFileName:string, ?properties, ?enableLogging) = new FSharpProjectFileInfo(fsprojFileName, ?properties=properties, ?enableLogging=enableLogging)


  let getOptions file enableLogging properties =
    let rec getOptions file : Option<string> * ProjectOptions =
      let parsedProject = FSharpProjectFileInfo.Parse(file, properties=properties, enableLogging=enableLogging)
      let referencedProjectOptions =
        [| for file in parsedProject.ProjectReferences do
             if Path.GetExtension(file) = ".fsproj" then
                match getOptions file with
                | Some outFile, opts -> yield outFile, opts
                | None, _ -> () |]

      let options = { ProjectFile = file
                      Options = Array.ofList parsedProject.Options
                      ReferencedProjectOptions = referencedProjectOptions
                      LogOutput = parsedProject.LogOutput }
      parsedProject.OutputFile, options

    snd (getOptions file)

  let addMSBuildv14BackupResolution () =
    let onResolveEvent = new ResolveEventHandler(fun sender evArgs ->
      let requestedAssembly = AssemblyName(evArgs.Name)
      if requestedAssembly.Name.StartsWith("Microsoft.Build") &&
          not (requestedAssembly.Name.EndsWith(".resources")) then
        requestedAssembly.Version <- Version("14.0.0.0")
        Assembly.Load (requestedAssembly)
      else
        null)
    AppDomain.CurrentDomain.add_AssemblyResolve(onResolveEvent)

  let rec pairs l =
    match l with
    | [] | [_] -> []
    | x::y::rest -> (x,y) :: pairs rest

  [<EntryPoint>]
  let main argv =
      let ret, opts =
          try
              addMSBuildv14BackupResolution ()
              if argv.Length >= 2 then
                let projectFile = argv.[0]
                let enableLogging = match Boolean.TryParse(argv.[1]) with
                                    | true, true -> true
                                    | _ -> false
                let props = pairs (List.ofArray argv.[2..])
                let opts = getOptions argv.[0] enableLogging props
                0, opts
              else
                1, { ProjectFile = ""; Options = [||]; ReferencedProjectOptions = [||]; LogOutput = "At least two arguments required." }
          with e ->
                2, { ProjectFile = ""; Options = [||]; ReferencedProjectOptions = [||]; LogOutput = e.ToString() }

      let ser = new DataContractJsonSerializer(typeof<ProjectOptions>)
      ser.WriteObject(Console.OpenStandardOutput(), opts)
      ret
