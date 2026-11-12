using System.Xml;
using System.Xml.Linq;
using Verse;

namespace StrategicWork.RimWorld;

// Route the assembled Def XML before RimWorld caches WorkGivers. Third-party task IDs are optional.
public sealed class NativeJobPatch : PatchOperation
{
    protected override bool ApplyWorker(XmlDocument xml)
    {
        if (ModsConfig.IsActive("frozensnowfox.complexjobs") || ModsConfig.IsActive("frozensnowfox.complexjobslegacy")) return true;
        var pack = LoadedModManager.RunningModsListForReading.First(m =>
            m.PackageIdPlayerFacing.Equals("rimlazy.strategicwork", StringComparison.OrdinalIgnoreCase));
        var catalog = XElement.Load(Path.Combine(pack.RootDir, "WorkDetails", "Catalog.xml"));
        var language = (Prefs.LangFolderName ?? "").Split('(', 2)[0].Trim();
        var optionalLabels = XElement.Load(Path.Combine(pack.RootDir, "WorkDetails", "OptionalWorkLabels.xml"))
            .Elements("language").FirstOrDefault(l => (string?)l.Attribute("id") == language);
        var jobTable = new JobXmlTable(xml);
        jobTable.Apply(catalog, NativeJobSettings.Read(), optionalLabels);
        return true;
    }
}

internal sealed class JobXmlTable
{
    private readonly XmlElement defs;
    private readonly Dictionary<(string Type, string Id), XmlElement> indices = new();
    internal JobXmlTable(XmlDocument document)
    {
        defs = document.DocumentElement ?? throw new InvalidDataException("Game XML has no Defs element");
        foreach (var node in defs.ChildNodes.OfType<XmlElement>())
        {
            if (node["defName"] is {} name) indices[(node.Name, name.InnerText)] = node;
            if (node.HasAttribute("Name")) indices[(node.Name, "@" + node.GetAttribute("Name"))] = node;
        }
    }

    internal void Apply(XElement catalog, HashSet<string> enabled, XElement? optionalLabels)
    {
        var columns = catalog.Element("columns")!.Elements().ToArray();
        foreach (var column in columns)
        {
            var id = A(column, "id");
            if (column.Attribute("optional") == null || enabled.Contains(id)) continue;
            if (indices.Remove(("WorkTypeDef", id), out var item)) defs.RemoveChild(item);
        }
        // DefInjected requires every target Def to exist. Set these conditional fields before parsing
        // the surviving WorkTypeDefs; later translation packs can still override them normally.
        if (optionalLabels != null)
        {
            foreach (var text in optionalLabels.Elements("work"))
            {
                string id = A(text, "id");
                if (!enabled.Contains(id) || Get("WorkTypeDef", id) is not { } def) continue;
                foreach (var field in text.Elements()) Set(def, field.Name.LocalName, field.Value);
            }
        }
        foreach (var group in catalog.Elements("routes"))
        {
            if (group.Attribute("option") is {} opt && !enabled.Contains(opt.Value)) continue;
            foreach (var rule in group.Elements()) ApplyRoute(rule);
        }
        AddCapabilities(catalog.Element("capabilities")!.Elements(), columns);
        ApplyPolicy(enabled);
    }

    private void ApplyRoute(XElement rule)
    {
        string id = A(rule,"id"), work = A(rule,"work");
        switch (rule.Name.LocalName)
        {
            case "task":
                if (Get("WorkGiverDef", id) is not {} task) return;
                if (work.Length > 0 && Get("WorkTypeDef", work) != null) Set(task, "workType", work);
                if (rule.Attribute("order") is {} order) Set(task, "priorityInType", order.Value);
                if (rule.Attribute("mechs") is {} mechs) Set(task, "canBeDoneByMechs", mechs.Value);
                break;
            case "order":
                if (Get("WorkTypeDef", work) is {} col) Set(col, "naturalPriority", A(rule,"rank"));
                break;
            case "recipe":
                if (Get("RecipeDef", id) is not {} recipe) return;
                if (work == "-") recipe["requiredGiverWorkType"]?.ParentNode?.RemoveChild(recipe["requiredGiverWorkType"]!);
                else if (Get("WorkTypeDef", work) != null) Set(recipe, "requiredGiverWorkType", work);
                break;
            case "additionalTask": AddTask(rule); break;
            case "detachBench":
                var stations = Get("WorkGiverDef", A(rule,"task"))?["fixedBillGiverDefs"];
                var station = stations?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.InnerText == A(rule,"bench"));
                if (station != null) stations!.RemoveChild(station);
                break;
        }
    }

    private void AddTask(XElement rule)
    {
        var work = A(rule, "work");
        var id = A(rule, "id");
        var column = Get("WorkTypeDef", work);
        if (column == null || Get("WorkGiverDef",id) != null) return;
        if (rule.Attribute("requires") is {} req && !req.Value.Split(',').All(ModsConfig.IsActive)) return;
        if (rule.Attribute("any") is {} any && !any.Value.Split(',').Any(ModsConfig.IsActive)) return;
        var benches = Words(A(rule,"benches")).Where(b => Get("ThingDef", b) != null).ToArray();
        if (rule.Attribute("benches") != null && benches.Length == 0) return;
        var item = Append(defs, "WorkGiverDef");
        indices[(item.Name,id)] = item;
        Set(item, "defName", id);
        Set(item, "giverClass", A(rule,"kind"));
        Set(item, "priorityInType", A(rule,"order"));
        Set(item, "workType", work);
        // Use job-specific English strings where the base game has matching behavior, with a proper fallback.
        string label = column["labelShort"]?.InnerText ?? work;
        var model = defs.ChildNodes.OfType<XmlElement>().FirstOrDefault(candidate =>
            candidate.Name == "WorkGiverDef" && candidate != item &&
            candidate["giverClass"]?.InnerText == A(rule,"kind") &&
            (benches.Length == 0 || candidate["fixedBillGiverDefs"]?.ChildNodes.OfType<XmlElement>().Any(b => benches.Contains(b.InnerText)) == true));
        foreach (var key in new[] { "label", "verb", "gerund" })
            Set(item, key, model?[key]?.InnerText ?? label.ToLowerInvariant());
        foreach (var key in new[] { "directOrderable", "prioritizeSustains", "equivalenceGroup", "nonColonistsCanDo", "canBeDoneWhileDrafted" })
            if (rule.Attribute(key) is {} flag) Set(item, key, flag.Value);
        foreach (var need in Words(A(rule,"needs"))) AddListValue(Child(item,"requiredCapacities"),need);
        foreach (var bench in benches) AddListValue(Child(item,"fixedBillGiverDefs"),bench);
    }

    private void AddCapabilities(IEnumerable<XElement> grants, IEnumerable<XElement> columns)
    {
        // Preserve abilities of biotech workers, modded robots and restricted species after splitting a job.
        var lists = Select(defs, "ThingDef/race/mechEnabledWorkTypes | */modExtensions/li/inherentWorkTypes | */modExtensions/li/forbiddenWorkTypes | ThingDef/comps/li/enabledWorkTypes | ThinkTreeDef[defName='VQE_Drone']//li[@Class='VanillaQuestsExpandedDroneFactory.JobGiver_DoWork']/workTypes");
        var humanAges = Get("ThingDef","Human")?["race"]?["lifeStageWorkSettings"];
        var gene = Get("GeneDef","VRE_EarlyMaturity");
        var earlyAges = gene?.SelectSingleNode("modExtensions/li[@Class='VREArchon.LifeStageWorkSettingsExtension']/lifeStageWorkSettings") as XmlElement;
        foreach (var spec in columns)
        {
            var id = A(spec,"id");
            if (Get("WorkTypeDef",id) == null) continue;
            if (humanAges != null && spec.Attribute("childAge") is {} age) Set(humanAges,id,age.Value);
            if (earlyAges != null && spec.Attribute("earlyAge") is {} early) Set(earlyAges,id,early.Value);
            var parents = Words(A(spec,"inherits"));
            foreach (var list in lists)
            {
                var parent = list.ChildNodes.OfType<XmlElement>().Select(e => e.InnerText).FirstOrDefault(parents.Contains);
                if (parent == null) continue;
                AddListValue(list,id);
                if (list.Name == "mechEnabledWorkTypes" && list.ParentNode?["mechWorkTypePriorities"] is {} ranks)
                    SetIfMissing(ranks, id, ranks[parent]?.InnerText ?? "3");
            }
        }
        foreach (var grant in grants)
        {
            var def = Get(A(grant,"type"),A(grant,"id"));
            if (def == null) continue;
            var field = A(grant,"field");
            var owner = def;
            if (grant.Attribute("component") is {} c)
                owner = Select(def, "comps/li | modExtensions/li").FirstOrDefault(e => e.GetAttribute("Class") == c.Value);
            else if (field is "mechEnabledWorkTypes" or "mechWorkTypePriorities") owner = def["race"];
            if (owner == null) continue;
            var target = Child(owner,field);
            foreach (var pair in Words(A(grant,"jobs")))
            {
                var parts = pair.Split(':');
                var id = parts[0];
                if (Get("WorkTypeDef", id) == null) continue;
                if (field == "mechWorkTypePriorities") SetIfMissing(target,id,parts[1]);
                else if (field == "robotWorkTypes")
                {
                    if (Select(target,"li/workTypeDef").Any(n => n.InnerText == id)) continue;
                    var entry = Append(target,"li"); Set(entry,"workTypeDef",id); Set(entry,"priority",parts[1]);
                }
                else AddListValue(target,id);
            }
        }
    }

    private void ApplyPolicy(HashSet<string> options)
    {
        // Warcaskets still need basic transport work after the old Hauling column is divided.
        RemoveAll("TraitDef[defName='VFEP_WarcasketTrait' or defName='VFEP_WarcasketTrait_Mech']/disabledWorkTags/li[.='ManualDumb']");
        if (options.Contains("FSFDraftedWorkTasks"))
            foreach (var job in Select(defs,"WorkGiverDef")) Set(job,"canBeDoneWhileDrafted","true");
        if (options.Contains("FSFSlavesAreCapable")) RemoveAll("WorkTypeDef[defName!='Warden']/disabledForSlaves");
        if (options.Contains("FSFIdeoRolesAreCapable"))
            RemoveAll("PreceptDef/roleDisabledWorkTags | PreceptDef[defName='IdeoRole_ShootingSpecialist' or defName='IdeoRole_MeleeSpecialist']/roleEffects/li[@Class='RoleEffect_NoMeleeWeapons' or @Class='RoleEffect_NoRangedWeapons']");
        if (options.Contains("FSFWarcasketsAreCapable"))
            RemoveAll("TraitDef[defName='VFEP_WarcasketTrait' or defName='VFEP_WarcasketTrait_Mech']/disabledWorkTags");
        if (options.Contains("FSFAnomalyTasksInDarkStudy") && Get("WorkTypeDef","DarkStudy") is {} study)
        {
            AddListValue(Child(study,"relevantSkills"),"Social");
            AddListValue(Child(study,"workTags"),"Social");
        }
    }

    private void RemoveAll(string path)
    {
        foreach (var node in Select(defs, path)) node.ParentNode!.RemoveChild(node);
    }
    private XmlElement? Get(string type,string id) => indices.GetValueOrDefault((type,id));
    private static XmlElement[] Select(XmlElement e,string path) => e.SelectNodes(path)!.OfType<XmlElement>().ToArray();
    private static string A(XElement e,string name) => (string?)e.Attribute(name) ?? "";
    private static string[] Words(string text) => text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
    private static XmlElement Append(XmlElement e,string name) => (XmlElement)e.AppendChild(e.OwnerDocument!.CreateElement(name))!;
    private static XmlElement Child(XmlElement e,string name) => e[name] ?? Append(e,name);
    private static void Set(XmlElement e,string name,string value) => Child(e,name).InnerText = value;
    private static void SetIfMissing(XmlElement e,string name,string value) { if (e[name] == null) Set(e,name,value); }
    private static void AddListValue(XmlElement list,string value)
    {
        if (!list.ChildNodes.OfType<XmlElement>().Any(n => n.Name == "li" && n.InnerText == value))
            Append(list,"li").InnerText=value;
    }
}
