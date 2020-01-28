module Tests.YamlRunner.TestsReader

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Linq

open System.Collections.Specialized
open Elasticsearch.Net
open Elasticsearch.Net
open Elasticsearch.Net.Specification.CatApi
open Elasticsearch.Net.Specification.MachineLearningApi
open System.IO
open Tests.YamlRunner
open Tests.YamlRunner.Models
open Tests.YamlRunner.Models
open Tests.YamlRunner.TestsLocator


let private tryPick<'a> (map:YamlMap) key =
    let found, value =  map.TryGetValue key
    if (found) then
        map.Remove key |> ignore
        Some (value :?> 'a)
        
    else None
    
let private tryPickList<'a,'b> (map:YamlMap) key parse =
    let found, value =  map.TryGetValue key
    if (found) then
        let value =
            value :?> List<Object>
            |> Seq.map (fun o -> o :?> 'a)
            |> Seq.map parse
            |> Seq.toList<'b>
        map.Remove key |> ignore
        Some value
    else None
    
let private pick<'a> (map:YamlMap) key =
    let found, value =  map.TryGetValue key
    if (found) then
        map.Remove key |> ignore
        (value :?> 'a)
    else failwithf "expected to find %s of type %s" key typeof<'a>.Name

let private mapSkip (operation:YamlMap) =
    let version = tryPick<string> operation "version" 
    let reason = tryPick<string> operation "reason"
    let parseFeature s = match s with | ToFeature s -> s
    let features =
        let found, value= operation.TryGetValue "features"
        match (found, value) with
        | (false, _) -> None
        | (_, x) ->
            match x with 
            | :? List<Object> -> tryPickList<string, Feature> operation "features" parseFeature
            | :? String as feature -> Some [parseFeature feature]
            | _ -> None
    let versionRange =
        match version with
        | Some "all"
        | Some "All" -> Some <| SemVer.Range("0.0.0 - 100.0.0")
        | Some v ->
            let range =
                let range = Regex.Replace(v, @"^\s*?-", "0.0.0 -")
                Regex.Replace(range, @"-\s*?$", "- 100.0.0")
            Some <| SemVer.Range(range)
        | None -> None
        
    Skip { Version=versionRange; Reason=reason; Features=features }
    
let private mapNumericAssert (operation:YamlMap) =
    operation
        |> Seq.map (fun (kv) ->
            let v =
                match kv.Value with
                | :? int32 as i -> Long <| Convert.ToInt64 i
                | :? int64 as i -> Long i
                | :? double as i -> Double <| Convert.ToDouble i
                | :? string as i -> NumericId <| StashedId.Create i
                | _ -> failwithf "unsupported %s" (kv.Value.GetType().Name)
            AssertOn.Create (kv.Key :?> string), v
        )
        |> Map.ofSeq
        
let private firstValueAsPath (operation:YamlMap) = AssertOn.Create (operation.Values.First() :?> string)

let private mapMatch (operation:YamlMap) =
    operation
        |> Seq.map (fun (kv) -> AssertOn.Create(kv.Key :?> string), AssertValue.FromObject kv.Value)
        |> Map.ofSeq
        
let private mapTransformAndSet (operation:YamlMap) =
    operation
        |> Seq.map (fun (kv) -> StashedId.Create  (kv.Key :?> string), SetTransformation.Create (kv.Value :?> string))
        |> Map.ofSeq
        
let private mapSet (operation:YamlMap) =
    operation
        |> Seq.map (fun (kv) -> ResponseProperty (kv.Key :?> string), StashedId.Create (kv.Value :?> string))
        |> Map.ofSeq
        
let private mapNodeSelector (operation:YamlMap) =
    let version = tryPick<string> operation "version" 
    let attribute = tryPick<YamlMap> operation "attribute"
    match (version, attribute) with
    | (version, Some attribute) ->
        let kv = attribute.First()
        let key = kv.Key :?> string
        let value = kv.Value :?> string
        match version with
        | Some version -> Some <| VersionAndAttributeSelector (version, key, value)
        | None -> Some <| NodeAttributeSelector(key, value)
    | (Some version, None) -> Some <| NodeVersionSelector(version)
    | _ -> None
            
    
let private mapDo section (operation:YamlMap) =
    
    let catch =
        match tryPick<string> operation "catch" with
        | Some s ->
            match s with | IsDoCatch s -> Some s | _ -> None
        | _ -> None
        
    let headers =
        match tryPick<YamlMap> operation "headers" with
        | Some map ->
            Some <| (map
                |> Seq.map (fun (kv) -> kv.Key :?> string, kv.Value :?> string)
                |> Map.ofSeq
                |> Map.fold
                  (fun (nv:NameValueCollection) k v ->
                    (nv.[k] <- v)
                    nv
                  )
                  (NameValueCollection())
           )
        | None -> None
    
    let warnings = tryPickList<string, string> operation "warnings" id
    let nodeSelector = mapNodeSelector operation
    
    let last = operation.Last()
    let lastKey = last.Key :?> string
    let lastValue =
        last.Value :?> YamlMap
    
    Do {
        ApiCall = (lastKey, lastValue)
        Catch = catch
        Warnings = warnings
        NodeSelector = nodeSelector
        Headers = headers
        AutoFail = match section with | "setup" | "teardown" -> true | _ -> false
    }
    
let private mapOperation section (operation:YamlMap) =
    let kv = operation.First();
    let key = kv.Key :?> string
    let yamlValue : YamlValue =
        match kv.Value with
        | :? YamlMap as m -> YamlDictionary m
        | :? string as s -> YamlString s
        | _ -> failwithf "unsupported %s" (kv.Value.GetType().Name)
    
    match key with
    | IsOperation s ->
        match (s, yamlValue) with
        | ("skip", YamlDictionary map) -> mapSkip map
        | ("set", YamlDictionary map) -> Set <| mapSet map
        | ("transform_and_set", YamlDictionary map) -> TransformAndSet <| mapTransformAndSet map
        | ("do", YamlDictionary map) -> mapDo section map
        | ("match", YamlDictionary map) ->  Assert <| Match (mapMatch map)
        | ("is_false", YamlDictionary map) -> Assert <| IsFalse (firstValueAsPath map)
        | ("is_true", YamlDictionary map) -> Assert <| IsTrue (firstValueAsPath map)
        | ("is_false", YamlString str) -> Assert <| IsFalse (AssertOn.Create str)
        | ("is_true", YamlString str) -> Assert <| IsTrue (AssertOn.Create str)
        | (IsNumericAssert n, YamlDictionary map) -> Assert <| NumericAssert (n, mapNumericAssert map)
        | (k, v) -> failwithf "%s does not support %s" k (v.GetType().Name)
    | unknownOperation -> Unknown unknownOperation
    
let private mapOperations section (operations:YamlMap list) =
    operations |> List.map (mapOperation section)
    
let private mapDocument (document:Dictionary<string, Object>) =
    
    let kv = document.First();
    let key = kv.Key
    let values = kv.Value :?> List<Object>
    
    let operations = values |> Enumerable.Cast<YamlMap> |> Seq.toList |> mapOperations key
    
    match key with
    | "setup" -> Setup operations 
    | "teardown" -> Teardown operations
    | name -> YamlTest { Name=name; Operations=operations }

type YamlTestDocument = {
    FileInfo: FileInfo
    Setup: Operations option
    Teardown: Operations option
    Tests: YamlTest list
}

let private DefaultSetup : Operation list = [Actions("Setup", fun (client, suite) ->
    match suite with
    | OpenSource ->
        let x = client.Indices.Delete<VoidResponse>("*")
        let templates =
            client.Cat.Templates<StringResponse>("*", CatTemplatesRequestParameters(Headers=["name"].ToArray()))
                .Body.Split("\n")
                |> Seq.filter(fun f -> not(String.IsNullOrWhiteSpace(f)) && not(f.StartsWith(".")) && f <> "security-audit-log")
                //TODO template does not accept comma separated list but is documented as such
                |> Seq.map(fun template -> client.Indices.DeleteTemplateForAll<VoidResponse>(template).Success)
                |> Seq.fold (fun state b -> state && b) true
        x.Success && templates
    | XPack ->
        let data = PostData.String @"{""password"":""x-pack-test-password"", ""roles"":[""superuser""]}"
        let putUser = client.Security.PutUser<StringResponse>("x_pack_rest_user", data)
        
        let closeJobs = client.MachineLearning.CloseJob<StringResponse>("_all", PostData.Empty)
        let getJobs = client.MachineLearning.GetJobs<DynamicResponse>("_all")
        if (getJobs.Get<int64> "count" > 0L) then
            let jobs = getJobs.Get<string[]> "jobs.job_id"
            ignore()
        
        
        client.Indices.Refresh<VoidResponse>("_all") |> ignore
        putUser.Success
)]

let private toDocument (yamlInfo:YamlFileInfo) (sections:YamlTestSection list) =
    let setups = (sections |> List.tryPick (fun s -> match s with | Setup s -> Some s | _ -> None)) 
    {
        FileInfo = FileInfo yamlInfo.File
        Setup = Some <| (DefaultSetup @ (setups |> Option.defaultValue []))
        Teardown = sections |> List.tryPick (fun s -> match s with | Teardown s -> Some s | _ -> None)
        Tests = sections |> List.map (fun s -> match s with | YamlTest s -> Some s | _ -> None) |> List.choose id
    }

type YamlTestFolder = { Folder: string; Files: YamlTestDocument list } 

let ReadYamlFile (yamlInfo:YamlFileInfo) = 

    let serializer = SharpYaml.Serialization.Serializer()
    let sections =
        let r e message = raise <| Exception(message, e)
        Regex.Split(yamlInfo.Yaml, @"---\s*?\r?\n")
        |> Seq.filter (fun s -> not <| String.IsNullOrWhiteSpace s)
        |> Seq.map (fun sectionString ->
            try
                (sectionString, serializer.Deserialize<Dictionary<string, Object>> sectionString)
            with | e -> r e <| sprintf "parseError %s: %s %s %s" yamlInfo.File e.Message Environment.NewLine sectionString
        )
        |> Seq.filter (fun (s, _) -> s <> null)
        |> Seq.map (fun (s, document) ->
            try
                mapDocument document
            with | e -> r e <| sprintf "mapError %s: %O %O %O" yamlInfo.File (e.Message) Environment.NewLine s
        )
        |> Seq.toList
        |> toDocument yamlInfo
        
    sections 

