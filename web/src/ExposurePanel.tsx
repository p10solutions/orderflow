import { useEffect, useState } from 'react';

type Exposure = { symbol: string; exposure: number; limit: number };
const symbols = ['PETR4', 'VALE3', 'VIIA4'];
const money = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const percent = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });

function decode(value: unknown): Exposure[] {
  if (!value || typeof value !== 'object' || !('items' in value) || !Array.isArray(value.items)) throw Error();
  const items = value.items as unknown[];
  if (items.length !== 3) throw Error();
  const decoded = items.map(item => {
    if (!item || typeof item !== 'object') throw Error();
    const row = item as Record<string, unknown>;
    if (typeof row.symbol !== 'string' || !symbols.includes(row.symbol) ||
        typeof row.exposure !== 'number' || !Number.isFinite(row.exposure) ||
        row.limit !== 100000000 || Math.abs(row.exposure) > row.limit) throw Error();
    return { symbol: row.symbol, exposure: row.exposure, limit: row.limit };
  });
  if (new Set(decoded.map(item => item.symbol)).size !== 3) throw Error();
  return decoded.sort((a, b) => a.symbol.localeCompare(b.symbol));
}

export function ExposurePanel({ revision }: { revision: string }) {
  const [items, setItems] = useState<Exposure[] | null>(null);
  const [updated, setUpdated] = useState<Date | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState(true);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout>;
    let controller: AbortController;
    async function load() {
      controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 4500);
      setBusy(true);
      try {
        const response = await fetch('/exposures', { signal: controller.signal, cache: 'no-store' });
        if (!response.ok) throw Error();
        const snapshot = decode(await response.json());
        if (active) {
          setItems(snapshot);
          setUpdated(new Date());
          setFailed(false);
        }
      } catch {
        if (active) setFailed(true);
      } finally {
        clearTimeout(timeout);
        if (active) {
          setBusy(false);
          timer = setTimeout(() => void load(), 5000);
        }
      }
    }
    void load();
    return () => { active = false; clearTimeout(timer); controller?.abort(); };
  }, [revision, refresh]);

  return <section className="exposure-panel" aria-label="Exposição por ativo">
    <div className="exposure-heading">
      <div><div className="eyebrow">CONTROLE DE RISCO</div><h2>Exposição por ativo</h2></div>
      <button type="button" className="secondary" disabled={busy} onClick={() => setRefresh(value => value + 1)}>
        {busy ? 'Atualizando…' : 'Atualizar exposição'}
      </button>
    </div>
    <p className="exposure-description">Compras somam, vendas subtraem. Limite de ±R$ 100 milhões por ativo.</p>
    <p className="exposure-status" role="status">
      {failed ? (items ? 'Atualização indisponível. Exibindo a última consulta; os valores podem estar desatualizados.' : 'Exposição indisponível. Aguardando conexão para consultar os saldos.')
        : updated ? `Última consulta às ${updated.toLocaleTimeString('pt-BR')}. Atualização automática a cada 5 segundos.`
          : 'Consultando exposição…'}
    </p>
    <div className="exposure-grid">
      {symbols.map(symbol => {
        const item = items?.find(row => row.symbol === symbol);
        const used = item ? Math.abs(item.exposure) / item.limit * 100 : null;
        return <article key={symbol} className={`exposure-asset ${failed ? 'stale' : ''}`}>
          <div className="exposure-symbol">{symbol}<span>BRL</span></div>
          <strong className="exposure-value">{item ? money.format(item.exposure) : '—'}</strong>
          <div className="exposure-direction">{item ? item.exposure > 0 ? 'Saldo comprador' : item.exposure < 0 ? 'Saldo vendedor' : 'Sem exposição' : 'Saldo não consultado'}</div>
          <progress max={100} value={used ?? 0} aria-label={`Limite utilizado de ${symbol}`} />
          <div className="exposure-utilization">{used === null ? '—' : `${percent.format(used)}%`} do limite utilizado</div>
        </article>;
      })}
    </div>
  </section>;
}