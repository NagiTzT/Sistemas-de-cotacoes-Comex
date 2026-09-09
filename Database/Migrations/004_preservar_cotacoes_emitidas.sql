begin;
alter table public.cot_cotacoes add column if not exists pdf_gerado_em_utc timestamptz;
alter table public.cot_cotacoes add column if not exists cotacao_origem_id uuid;
comment on column public.cot_cotacoes.pdf_gerado_em_utc is 'Primeira geração bem-sucedida de PDF. Edições posteriores criam outra cotação.';
comment on column public.cot_cotacoes.cotacao_origem_id is 'Cotação preservada que originou esta nova proposta.';
commit;
