import sys
import os

# Adiciona o diretório raiz ao sys.path para importar app e módulos
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app import app
