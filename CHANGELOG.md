# Historique des versions

## 0.3.0 — 17/10/2026

- **Images des boss et monstres** : vignette dans les résultats de recherche (overlay et application), image en
  tête de la fiche dans l'overlay, grande image dans l'application avec les boutons « Ajouter une image… »,
  « Coller une image » et « Retirer l'image ». Sans image, une pastille avec l'initiale s'affiche.
- **Importateur DofusDB `--images`** : télécharge les images de tous les monstres et boss dans ton dossier local
  (usage personnel, jamais publiées). Un monstre DofusDB est fusionné avec le boss du même nom (ex. les cibles
  d'avis de recherche), qui récupère ainsi son image.
- **Prêt pour la signature** : l'application est publiée en un seul `DofusCompanion.exe` et utilise le SQLite
  signé de Windows ; c'est le seul fichier à signer.

## 0.2.0 — 17/10/2026

- **Nouveau style des étapes dans l'overlay** : étapes numérotées sur une frise, prérequis avec leur état
  (rempli / à vérifier), résultat attendu en vert avec la durée, pièges mortels en rouge, informations
  manquantes en italique.
- **Fiche d'un donjon = mécaniques de ses boss** : chercher « Mémoire d'Orukam » affiche directement les
  mécaniques du Roi Imagami (avant : « aucune mécanique documentée » pour tous les donjons).
- **Overlay visible dans Discord / OBS** : il n'est plus masqué des partages d'écran ; il se cache uniquement
  pendant la capture faite par Dofus Companion elle-même.
- Texte de l'overlay lisible : l'opacité ne s'applique plus qu'au fond, plus au texte.
- Raccourcis par défaut sur `Ctrl+Shift+F9/F10/F11` (n'empiètent plus sur les raccourcis des navigateurs).
- Recherche plus rapide sur les grosses bases.

## 0.1.0 — 17/10/2026

Première version : overlay, encyclopédie (131 donjons, 224 boss, 604 mécaniques sourcées), analyse de
captures d'écran (OCR Windows local), import/export de connaissances, interface FR/EN.
