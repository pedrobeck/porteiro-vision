-- ========================================================
-- SCHEMA SUPABASE: PORTEIRO VISION
-- Execute este script no SQL Editor do seu projeto Supabase
-- ========================================================

-- 1. Criação da tabela de câmeras vinculada aos usuários do Supabase Auth
create table if not exists public.cameras (
  id uuid default gen_random_uuid() primary key,
  user_id uuid references auth.users(id) on delete cascade not null default auth.uid(),
  name text not null,
  brand text default 'generic',
  host text not null,
  port integer default 554,
  username text default 'admin',
  password text default '',
  stream_type text default 'sub',
  custom_url text default '',
  ptz_enabled boolean default false,
  created_at timestamp with time zone default timezone('utc'::text, now()) not null
);

-- 2. Habilita Row Level Security (RLS) para isolamento por usuário
alter table public.cameras enable row level security;

-- 3. Políticas de Segurança (Cada usuário só vê, edita e deleta as suas próprias câmeras)
create policy "Usuários podem ver apenas suas próprias câmeras"
  on public.cameras for select
  using (auth.uid() = user_id);

create policy "Usuários podem cadastrar suas próprias câmeras"
  on public.cameras for insert
  with check (auth.uid() = user_id);

create policy "Usuários podem atualizar apenas suas próprias câmeras"
  on public.cameras for update
  using (auth.uid() = user_id);

create policy "Usuários podem excluir apenas suas próprias câmeras"
  on public.cameras for delete
  using (auth.uid() = user_id);

-- 4. Índice para buscas rápidas por usuário
create index if not exists idx_cameras_user_id on public.cameras(user_id);
