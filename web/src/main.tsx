import { ExposurePanel } from './ExposurePanel';
import { useState, type FormEvent } from 'react';
import { createRoot } from 'react-dom/client';
import { useConnection, useOrderIntent, type Input } from './useOrderIntent';
import './style.css';

const initial: Input = { symbol: 'PETR4', side: 'Buy', quantity: '100', price: '10.00' };
const labels = { Accepted: 'Ordem aceita', Rejected: 'Ordem rejeitada', PendingConfirmation: 'Aguardando confirmação' };

function App() {
  const [input, setInput] = useState<Input>(initial);
  const connected = useConnection();
  const order = useOrderIntent();
  const { result, message, errors, busy, pollEnded, locked } = order;

  function change<K extends keyof Input>(key: K, value: Input[K]) {
    setInput(previous => ({ ...previous, [key]: value }));
    order.clearError(key);
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    void order.submit(input);
  }

  return (
    <div className="shell">
      <header>
        <a className="brand" href="/" aria-label="OrderFlow início">
          <span className="brand-mark">o</span>OrderFlow<span className="brand-dot">.</span>
        </a>
        <div className="environment">AMBIENTE DE DEMONSTRAÇÃO</div>
      </header>
      <main>
        <section className="intro">
          <div className="eyebrow"><span /> MESA DE OPERAÇÕES</div>
          <h1>Sua próxima ordem.<br /><em>Com precisão.</em></h1>
          <p>Envie ordens para avaliação de exposição e acompanhe a decisão de risco em uma sessão FIX 4.4.</p>
          <div className="session">
            <span className={`status-dot ${connected ? 'online' : ''}`} />
            <strong>{connected === null ? 'Verificando sessão' : connected ? 'Sessão FIX conectada' : 'Sessão FIX desconectada'}</strong>
            <span>FIX 4.4</span>
          </div>
          <div className="note">
            <span>01 / 02</span>
            <p>Cada ordem passa pelo controle de exposição do Accumulator antes de receber o aceite.</p>
          </div>
        </section>
        <section className="workspace" aria-label="Enviar ordem">
          <div className="card-heading">
            <div><div className="eyebrow">ORDER GENERATOR</div><h2>Nova ordem</h2></div>
            <span className="card-icon">↗</span>
          </div>
          <form onSubmit={submit} noValidate>
            <fieldset disabled={locked}>
              <legend className="sr-only">Dados da ordem</legend>
              <label htmlFor="symbol">Ativo</label>
              <select id="symbol" value={input.symbol} onChange={e => change('symbol', e.target.value)} aria-invalid={!!errors.symbol} aria-describedby="symbol-error">
                <option>PETR4</option><option>VALE3</option><option>VIIA4</option>
              </select>
              <div id="symbol-error" className="field-error">{errors.symbol}</div>
              <label>Lado da operação</label>
              <div className="side-selector" role="group" aria-label="Lado da operação">
                <button type="button" className={input.side === 'Buy' ? 'selected buy' : ''} aria-pressed={input.side === 'Buy'} onClick={() => change('side', 'Buy')}>↗ Compra</button>
                <button type="button" className={input.side === 'Sell' ? 'selected sell' : ''} aria-pressed={input.side === 'Sell'} onClick={() => change('side', 'Sell')}>↘ Venda</button>
              </div>
              <div className="field-error">{errors.side}</div>
              <div className="field-row">
                <div>
                  <label htmlFor="quantity">Quantidade</label>
                  <input id="quantity" value={input.quantity} inputMode="numeric" onChange={e => change('quantity', e.target.value)} aria-invalid={!!errors.quantity} aria-describedby="quantity-error" />
                  <small>De 1 a 99.999 unidades</small>
                  <div id="quantity-error" className="field-error">{errors.quantity}</div>
                </div>
                <div>
                  <label htmlFor="price">Preço unitário</label>
                  <div className="money-input">
                    <span>R$</span>
                    <input id="price" value={input.price} inputMode="decimal" onChange={e => change('price', e.target.value)} aria-invalid={!!errors.price} aria-describedby="price-error" />
                  </div>
                  <small>Até R$ 999,99 · centavos exatos</small>
                  <div id="price-error" className="field-error">{errors.price}</div>
                </div>
              </div>
            </fieldset>
            {message && <p className="alert" role="alert">{message}</p>}
            {!result && (
              <button className="primary" type="submit" disabled={busy || connected !== true}>
                {busy ? 'Enviando ordem…' : order.hasIntent ? 'Tentar novamente' : 'Enviar ordem'}
                <span aria-hidden="true">→</span>
              </button>
            )}
            {!result && order.hasIntent && !busy && (
              <button className="secondary" type="button" onClick={order.newIntent}>Iniciar nova intenção de ordem</button>
            )}
          </form>
          {result && (
            <div className={`result ${result.status}`} role="status" aria-live="polite">
              <span className="result-kicker">{result.status === 'PendingConfirmation' ? 'ACOMPANHAMENTO DA INTENÇÃO' : 'RESPOSTA DO ACCUMULATOR'}</span>
              <h3>{labels[result.status]}</h3>
              <p>{result.reason || (result.status === 'Accepted'
                ? 'Aceite registrado. Esta resposta não representa execução de uma negociação.'
                : result.status === 'Rejected'
                  ? 'A ordem não foi incorporada à exposição.'
                  : 'O prazo de espera não cancela a ordem enviada.')}</p>
              {result.diagnostic && <p className="alert">Diagnóstico técnico: {result.diagnostic}</p>}
              <dl>
                <dt>ClOrdID</dt><dd>{result.id}</dd>
                {result.orderId && <><dt>OrderID</dt><dd>{result.orderId}</dd></>}
                {result.execId && <><dt>ExecID</dt><dd>{result.execId}</dd></>}
              </dl>
              {result.status === 'PendingConfirmation' && pollEnded && <p>A consulta automática terminou. A ordem continua pendente de confirmação.</p>}
              <div className="result-actions">
                {result.status === 'PendingConfirmation' && (
                  <>
                    <button type="button" className="secondary" onClick={() => void order.query()}>Consultar status</button>
                    <button type="button" className="secondary" disabled={busy || connected !== true} onClick={() => void order.submit(input)}>
                      {busy ? 'Enviando ordem…' : 'Tentar novamente com a mesma identidade'}
                    </button>
                  </>
                )}
                <button type="button" className="secondary" disabled={busy} onClick={order.newIntent}>Nova ordem →</button>
              </div>
            </div>
          )}
          <div className="card-footer"><span>◈</span> Validação de risco por ativo <span className="footer-right">BRL</span></div>
        </section>
        <ExposurePanel revision={`${result?.id}:${result?.status}:${connected}`} />
      </main>
      <footer><span>ORDERFLOW</span><span>Precisão em cada conexão.</span></footer>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<App />);

