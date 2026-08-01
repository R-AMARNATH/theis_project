using System;
using System.Collections.Generic;
using CarbonAware.Core;

namespace CarbonAware.RegionMap;

public sealed class StaticRegionMapper : IRegionMapper
{
    private readonly Dictionary<(string cloud, string region), string> _map =
        new(ValueTupleComparer.Instance)
    {
            {("gcp","us-west1"),"BPA"},
            {("gcp","us-west2"),"LDWP"},
            {("gcp","us-west3"),"PACE"},
            {("gcp","us-west4"),"NEVP"},
            {("gcp","us-central1"),"MISO_WORTHINGTON"},
            {("gcp","us-east1"),"SC"},
            {("gcp","us-east4"),"PJM_DC"},
            {("gcp","us-east5"),"PJM_SOUTHWEST_OH"},
            {("gcp","us-south1"),"ERCOT_NORTHCENTRAL"},
            {("gcp","northamerica-northeast1"),"HQ"},
            {("gcp","northamerica-northeast2"),"IESO_NORTH"},
            {("gcp","northamerica-south1"),"MX_SIN"},
            {("gcp","southamerica-east1"),"BRA"},
            {("gcp","southamerica-west1"),"CHL"},
            {("gcp","europe-west1"),"BE"},
            {("gcp","europe-west2"),"UK"},
            {("gcp","europe-west3"),"DE"},
            {("gcp","europe-west4"),"NL"},
            {("gcp","europe-west6"),"CH"},
            {("gcp","europe-west8"),"IT"},
            {("gcp","europe-west9"),"FR"},
            {("gcp","europe-west10"),"DE"},
            {("gcp","europe-west12"),"IT"},
            {("gcp","europe-central2"),"PL"},
            {("gcp","europe-north1"),"FI"},
            {("gcp","europe-north2"),"SE"},
            {("gcp","europe-southwest1"),"ES"},
            {("gcp","me-west1"),"ISR"},
            {("gcp","me-central1"),"QAT"},
            {("gcp","me-central2"),"SAU"},
            {("gcp","africa-south1"),"ZAF"},
            {("gcp","asia-south1"),"IND"},
            {("gcp","asia-south2"),"IND"},
            {("gcp","asia-southeast1"),"SGP"},
            {("gcp","asia-southeast2"),"IDN"},
            {("gcp","asia-east1"),"TWN"},
            {("gcp","asia-east2"),"HKG"},
            {("gcp","asia-northeast1"),"JP_TK"},
            {("gcp","asia-northeast2"),"JP_KN"},
            {("gcp","asia-northeast3"),"KOR"},
            {("gcp","australia-southeast1"),"NEM_NSW"},
            {("gcp","australia-southeast2"),"NEM_VIC"},
            {("azure","austriaeast"),"AT"},
            {("azure","belgiumcentral"),"BE"},
            {("azure","eastus"),"PJM_DC"},
            {("azure","eastus2"),"PJM_DC"},
            {("azure","centralus"),"MISO_MASON_CITY"},
            {("azure","northcentralus"),"PJM_CHICAGO"},
            {("azure","southcentralus"),"ERCOT_SANANTONIO"},
            {("azure","westus"),"CAISO_NORTH"},
            {("azure","westus2"),"GCPD"},
            {("azure","westus3"),"AZPS"},
            {("azure","westcentralus"),"WACM"},
            {("azure","canadacentral"),"IESO_NORTH"},
            {("azure","canadaeast"),"HQ"},
            {("azure","mexicocentral"),"MX_SIN"},
            {("azure","brazilsouth"),"BRA"},
            {("azure","brazilsoutheast"),"BRA"},
            {("azure","chilecentral"),"CHL"},
            {("azure","northeurope"),"IE"},
            {("azure","westeurope"),"NL"},
            {("azure","ukSouth"),"UK"},
            {("azure","ukwest"),"UK"},
            {("azure","francecentral"),"FR"},
            {("azure","francesouth"),"FR"},
            {("azure","switzerlandnorth"),"CH"},
            {("azure","switzerlandwest"),"CH"},
            {("azure","germanywestcentral"),"DE"},
            {("azure","germanynorth"),"DE"},
            {("azure","norwayeast"),"NO"},
            {("azure","norwaywest"),"NO"},
            {("azure","swedencentral"),"SE"},
            {("azure","swedensouth"),"SE"},
            {("azure","polandcentral"),"PL"},
            {("azure","italynorth"),"IT"},
            {("azure","spaincentral"),"ES"},
            {("azure","uaenorth"),"ARE"},
            {("azure","uaecentral"),"ARE"},
            {("azure","qatarcentral"),"QAT"},
            {("azure","israelcentral"),"ISR"},
            {("azure","saudiarabiaeast"),"SAU"},
            {("azure","saudiarabiacentral"),"SAU"},
            {("azure","southafricanorth"),"ZAF"},
            {("azure","southafricawest"),"ZAF"},
            {("azure","eastasia"),"HKG"},
            {("azure","southeastasia"),"SGP"},
            {("azure","japaneast"),"JP_TK"},
            {("azure","japanwest"),"JP_KN"},
            {("azure","koreacentral"),"KOR"},
            {("azure","koreasouth"),"KOR"},
            {("azure","centralindia"),"IND"},
            {("azure","southindia"),"IND"},
            {("azure","westindia"),"IND"},
            {("azure","indonesiacentral"),"IDN"},
            {("azure","malaysiawest"),"MYS"},
            {("azure","taiwannorth"),"TWN"},
            {("azure","australiaeast"),"NEM_NSW"},
            {("azure","australiasoutheast"),"NEM_VIC"},
            {("azure","australiacentral"),"NEM_NSW"},
            {("azure","newzealandnorth"),"NZL"},
            {("aws","us-east-1"), "PJM_DC"},
            {("aws","us-east-2"), "PJM_SOUTHWEST_OH"},
            {("aws","us-west-1"), "CAISO_NORTH"},
            {("aws","us-west-2"), "BPA"},
            {("aws","us-gov-east-1"), "PJM_DC"}, //only if eligible
            {("aws","us-gov-west-1"), "SCL"}, //only if eligible
            {("aws","ca-central-1"), "HQ"},
            {("aws","ca-west-1"), "AESO"},
            {("aws","mx-central-1"), "MX_SIN"},
            {("aws","sa-east-1"), "BRA"},
            {("aws","eu-west-1"), "IE"},
            {("aws","eu-west-2"), "UK"},
            {("aws","eu-west-3"), "FR"},
            {("aws","eu-central-1"), "DE"},
            {("aws","eu-central-2"), "CH"},
            {("aws","eu-north-1"), "SE"},
            {("aws","eu-south-1"), "IT"},
            {("aws","eu-south-2"), "ES"},
            {("aws","il-central-1"), "ISR"},
            {("aws","me-south-1"), "BHR"},
            {("aws","me-central-1"), "ARE"},
            {("aws","af-south-1"), "ZAF"},
            {("aws","ap-east-1"), "HKG"},
            {("aws","ap-east-2"), "TWN"},
            {("aws","ap-southeast-1"), "SGP"},
            {("aws","ap-southeast-2"), "NEM_NSW"},
            {("aws","ap-southeast-3"), "IDN"},
            {("aws","ap-southeast-4"), "NEM_VIC"},
            {("aws","ap-southeast-5"), "MYS"},
            {("aws","ap-southeast-6"), "NZL"},
            {("aws","ap-southeast-7"), "THA"},
            {("aws","ap-south-1"), "IND"},
            {("aws","ap-south-2"), "IND"},
            {("aws","ap-northeast-1"), "JP_TK"},
            {("aws","ap-northeast-2"), "KOR"},
            {("aws","ap-northeast-3"), "JP_KN"}
};

    private sealed class ValueTupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly ValueTupleComparer Instance = new();
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
         && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }

    public string GetGridZones(string cloud, string region)
    {
        if (_map.TryGetValue((cloud, region), out var zone))
            return zone;
        return null;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ListAllRegionsByCloud()
    {
        var dict = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cloud, region) in _map.Keys)
        {
            if (!dict.TryGetValue(cloud, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dict[cloud] = set;
            }
            set.Add(region);
        }

        // return a stable, ordered read-only shape
        var res = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in dict)
        {
            var ordered = kv.Value.ToList();
            ordered.Sort(StringComparer.OrdinalIgnoreCase);
            res[kv.Key] = ordered;
        }
        return res;
    }

}