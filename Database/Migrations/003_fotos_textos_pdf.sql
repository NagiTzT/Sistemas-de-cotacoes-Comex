begin;
alter table public.cot_cotacoes add column if not exists prazos_pdf text;
alter table public.cot_cotacoes add column if not exists informacoes_pdf text;
alter table public.cot_itens add column if not exists foto bytea;
do $$ begin
    if not exists (select 1 from pg_constraint where conrelid = 'public.cot_itens'::regclass and conname = 'cot_itens_foto_tamanho') then
        alter table public.cot_itens add constraint cot_itens_foto_tamanho check (foto is null or octet_length(foto) <= 1048576);
    end if;
end $$;
commit;
