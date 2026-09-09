begin;

alter table public.cot_cotacoes
    add column if not exists data_referencia_cambio date,
    add column if not exists calculo_detalhado jsonb not null default '{}'::jsonb;

alter table public.cot_itens
    add column if not exists impostos_detalhados jsonb not null default '{}'::jsonb;

do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'cot_cotacoes_calculo_detalhado_objeto_ck'
          and conrelid = 'public.cot_cotacoes'::regclass
    ) then
        alter table public.cot_cotacoes
            add constraint cot_cotacoes_calculo_detalhado_objeto_ck
            check (jsonb_typeof(calculo_detalhado) = 'object');
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'cot_itens_impostos_detalhados_objeto_ck'
          and conrelid = 'public.cot_itens'::regclass
    ) then
        alter table public.cot_itens
            add constraint cot_itens_impostos_detalhados_objeto_ck
            check (jsonb_typeof(impostos_detalhados) = 'object');
    end if;
end $$;

comment on column public.cot_cotacoes.calculo_detalhado is
    'Parâmetros de logística, despesas aduaneiras, custos operacional/financeiro, markup e frete ao cliente.';
comment on column public.cot_itens.impostos_detalhados is
    'Alíquotas de importação e venda e seleção dos créditos fiscais por item/NCM.';

commit;
