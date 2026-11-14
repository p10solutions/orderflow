import { useCallback, useEffect, useRef, useState } from 'react';

export type Side = 'Buy' | 'Sell';
export type Input = { symbol: string; side: Side; quantity: string; price: string };
type Order = { id: string; symbol: string; side: Side; quantity: number; price: string };
export type Result = {
  id: string;
  status: 'Accepted' | 'Rejected' | 'PendingConfirmation';
  orderId?: string | null;
  execId?: string | null;
  reason?: string | null;
  diagnostic?: string | null;
};
type ApiError = { error?: string; code?: string; errors?: Record<string, string> };
export type Errors = Partial<Record<keyof Input, string>>;

export function validate(input: Input): Errors {
  const errors: Errors = {};
  if (!['PETR4', 'VALE3', 'VIIA4'].includes(input.symbol)) errors.symbol = 'Selecione um ativo permitido.';
  if (!['Buy', 'Sell'].includes(input.side)) errors.side = 'Selecione compra ou venda.';
  if (!/^\d+$/.test(input.quantity) || Number(input.quantity) < 1 || Number(input.quantity) > 99999) {
    errors.quantity = 'Informe um inteiro entre 1 e 99.999.';
  }
  const price = input.price.replace(',', '.');
  if (price.length > 32 || !/^[0-9]{1,4}(\.[0-9]{1,2}0*)?$/.test(price)) {
    errors.price = 'Use um preço em centavos exatos (ex.: 10,25).';
  } else {
    const [whole, fraction = ''] = price.split('.');
    const cents = BigInt(whole) * 100n + BigInt(fraction.padEnd(2, '0').slice(0, 2));
    if (cents <= 0n || cents >= 100000n) errors.price = 'O preço deve estar entre R$ 0,01 e R$ 999,99.';
  }
  return errors;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function isResult(value: unknown, id: string): value is Result {
  return isObject(value) && value.id === id
    && typeof value.status === 'string' && ['Accepted', 'Rejected', 'PendingConfirmation'].includes(value.status)
    && ['orderId', 'execId', 'reason', 'diagnostic'].every(key => value[key] == null || typeof value[key] === 'string');
}

function isApiError(value: unknown): value is ApiError {
  return isObject(value)
    && typeof value.error === 'string' && typeof value.code === 'string'
    && (value.errors == null || (isObject(value.errors) && Object.values(value.errors).every(error => typeof error === 'string')));
}

export function useConnection() {
  const [connected, setConnected] = useState<boolean | null>(null);
  useEffect(() => {
    let alive = true;
    const check = async () => {
      try {
        const response = await fetch('/status', { signal: AbortSignal.timeout(3000) });
        const data = await response.json();
        if (alive) setConnected(response.ok && data.connected === true);
      } catch {
        if (alive) setConnected(false);
      }
    };
    void check();
    const timer = window.setInterval(() => void check(), 5000);
    return () => { alive = false; window.clearInterval(timer); };
  }, []);
  return connected;
}

export function useOrderIntent() {
  const [result, setResult] = useState<Result | null>(null);
  const [message, setMessage] = useState('');
  const [errors, setErrors] = useState<Errors>({});
  const [busy, setBusy] = useState(false);
  const [pollEnded, setPollEnded] = useState(false);
  const [editable, setEditable] = useState(true);
  const intent = useRef<Order | null>(null);
  const generation = useRef(0);
  const submitting = useRef(false);
  const uncertain = useRef(false);
  const latestResult = useRef<Result | null>(null);

  const applyResult = useCallback((next: Result) => {
    // An older GET may finish after a retry has already obtained a final decision.
    if (latestResult.current?.status !== 'PendingConfirmation' && latestResult.current !== null) return;
    latestResult.current = next;
    setResult(next);
    if (next.status !== 'PendingConfirmation') {
      setMessage('');
      setPollEnded(false);
    }
  }, []);

  const hasFinalResult = useCallback(() => latestResult.current !== null && latestResult.current.status !== 'PendingConfirmation', []);

  const query = useCallback(async (id: string, version: number) => {
    const current = () => generation.current === version && intent.current?.id === id && !hasFinalResult();
    try {
      const response = await fetch(`/orders/${encodeURIComponent(id)}`, { signal: AbortSignal.timeout(3000) });
      if (!current()) return false;
      if (response.status === 404) {
        setMessage('Identidade ainda não encontrada. A entrega não está confirmada; tente novamente com a mesma identidade.');
        return false;
      }
      if (!response.ok) throw new Error('Query failed');
      const next: unknown = await response.json();
      if (!current()) return false;
      if (!isResult(next, id)) throw new Error('Invalid response');
      applyResult(next);
      setMessage('');
      return next.status !== 'PendingConfirmation';
    } catch {
      if (current()) setMessage('Falha na consulta. Preserve o identificador e tente novamente.');
      return false;
    }
  }, [applyResult, hasFinalResult]);

  useEffect(() => {
    if (result?.status !== 'PendingConfirmation') return;
    const id = result.id;
    const version = generation.current;
    let stopped = false;
    let attempts = 0;
    let timer: number;
    const poll = async () => {
      const final = await query(id, version);
      if (stopped || final || generation.current !== version || hasFinalResult()) return;
      attempts++;
      if (attempts < 20) timer = window.setTimeout(() => void poll(), 1000);
      else setPollEnded(true);
    };
    timer = window.setTimeout(() => void poll(), 1000);
    return () => { stopped = true; window.clearTimeout(timer); };
  }, [result?.id, result?.status, query, hasFinalResult]);

  useEffect(() => () => { generation.current++; }, []);

  async function submit(input: Input) {
    if (submitting.current || hasFinalResult()) return;
    const invalid = validate(input);
    setErrors(invalid);
    if (Object.keys(invalid).length) return;
    if (!intent.current || editable) {
      intent.current = {
        id: intent.current?.id ?? crypto.randomUUID(),
        symbol: input.symbol,
        side: input.side,
        quantity: Number(input.quantity),
        price: input.price.replace(',', '.'),
      };
    }
    const order = intent.current;
    const version = generation.current;
    const current = () => generation.current === version && intent.current?.id === order.id;
    submitting.current = true;
    setBusy(true);
    setEditable(false);
    setMessage('');
    try {
      const response = await fetch('/orders', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(order),
        signal: AbortSignal.timeout(15000),
      });
      const data: unknown = await response.json();
      if (!current() || hasFinalResult()) return;
      if (response.ok) {
        if (!isResult(data, order.id)) throw new Error('Invalid response');
        uncertain.current = true;
        applyResult(data);
      } else {
        if (!isApiError(data)) throw new Error('Invalid error response');
        const failure = data;
        if (response.status === 400 && !uncertain.current) {
          setEditable(true);
          setErrors(failure.errors ?? {});
          setMessage(failure.error ?? 'Não foi possível validar a ordem. Confira os campos.');
        } else if (response.status === 413 || response.status === 429) {
          setMessage(response.status === 413
            ? 'A requisição excedeu o tamanho permitido. Preserve a identidade e confira os campos.'
            : 'O serviço está ocupado. Aguarde e tente novamente com a mesma identidade.');
        } else if (failure.code === 'RegistryCapacityReached') {
          setMessage('A capacidade do registro foi atingida. Contate o operador; as ordens existentes continuam disponíveis e reiniciar os serviços não libera capacidade.');
        } else if (response.status === 503) {
          setMessage('Sessão FIX indisponível. Aguarde a conexão e tente novamente com a mesma identidade.');
        } else if (response.status === 409) {
          setMessage('Esta identidade já pertence a outra ordem. Preserve o identificador e confira a intenção antes de iniciar outra.');
        } else {
          throw new Error('Unconfirmed submission');
        }
      }
    } catch {
      if (current() && !hasFinalResult()) {
        uncertain.current = true;
        applyResult({ id: order.id, status: 'PendingConfirmation' });
        setMessage('A confirmação ainda não chegou. A ordem pode ter sido recebida; consulte ou tente novamente com a mesma identidade.');
      }
    } finally {
      if (current()) { submitting.current = false; setBusy(false); }
    }
  }

  function newIntent() {
    generation.current++;
    intent.current = null;
    latestResult.current = null;
    uncertain.current = false;
    submitting.current = false;
    setResult(null);
    setMessage('');
    setErrors({});
    setPollEnded(false);
    setBusy(false);
    setEditable(true);
  }

  function clearError(field: keyof Input) {
    setErrors(previous => ({ ...previous, [field]: undefined }));
  }

  return {
    result, message, errors, busy, pollEnded,
    locked: busy || !editable,
    hasIntent: intent.current !== null,
    submit, newIntent, clearError,
    query: () => intent.current && query(intent.current.id, generation.current),
  };
}


