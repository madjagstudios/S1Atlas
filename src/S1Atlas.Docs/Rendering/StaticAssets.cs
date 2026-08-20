using S1Atlas.Docs.Determinism;
using S1Atlas.Docs.Generation;

namespace S1Atlas.Docs.Rendering;

public sealed class StaticAssets
{
    private readonly DeterministicJsonWriter _json = new();

    public IReadOnlyDictionary<string, string> Render(IReadOnlyList<PortalSymbolModel> symbols) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["assets/site.css"] = "body{font-family:system-ui,sans-serif;line-height:1.5;max-width:1100px;margin:0 auto;padding:1rem;background:#f7f7f5;color:#222}header{padding:.75rem 0;border-bottom:1px solid #ccc}section{background:#fff;border:1px solid #ddd;border-radius:.35rem;padding:1rem;margin:1rem 0}code,pre{font-family:ui-monospace,monospace}pre{overflow:auto;background:#f1f1ed;padding:1rem}.fact{border-left:.3rem solid #477}.provenance{border-left:.3rem solid #468}.status{border-left:.3rem solid #c84}\n",
            ["assets/search-index.json"] = _json.WriteSearchIndexJson(symbols),
            ["assets/search-index.js"] = _json.WriteInlineSearchIndexJavaScript(symbols),
            ["assets/search.js"] = "document.addEventListener('DOMContentLoaded',()=>{const q=document.querySelector('#search-query'),o=document.querySelector('#search-results');if(!q||!o)return;const render=()=>{const term=q.value.toLowerCase();o.innerHTML=S1ATLAS_SEARCH_INDEX.filter(x=>(x.QualifiedName+' '+x.Signature+' '+x.ExactKey).toLowerCase().includes(term)).slice(0,200).map(x=>'<li><a href=\"'+x.Href+'\">'+x.QualifiedName+'</a> <small>'+x.Kind+'</small></li>').join('')};q.addEventListener('input',render);render()});\n"
        };
}
