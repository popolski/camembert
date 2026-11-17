# Camembert (version native)

Analyseur d'espace disque pour Windows 10 et 11 (x64) : C# / .NET 10 / WPF / SkiaSharp, sans serveur.
Gratuit, portable, hors ligne : ni installation, ni compte, ni télémétrie.
État : **lots 1 à 4** du cahier des charges.

## Lancer

**Version portable** : décompresser `Camembert-portable-win-x64.zip` n'importe où, puis double-cliquer sur
`Camembert.exe`. Le runtime .NET est inclus : rien à installer.

`Camembert.exe <dossier>` lance directement l'analyse de ce dossier (`Camembert.exe C:` analyse tout le disque C).

**Depuis les sources** (SDK .NET 10) :

```
dotnet build -c Release
dotnet test
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

`build\publish.ps1` produit `artifacts\Camembert-portable-win-x64.zip`. Le workflow `.github/workflows/build.yml`
fait de même sur GitHub (tests, puis ZIP en artefact ; un tag `v*` crée une version avec le ZIP joint).

Tests : le test du moteur NTFS sur disque réel est ignoré hors d'une session administrateur ; le test de la Corbeille
envoie de petits fichiers temporaires dans la Corbeille puis les en retire.

## Fichiers écrits par Camembert

Tous à côté de `Camembert.exe`, rien dans le registre :

- `Camembert.settings.json` : réglages (thème, animations), créé au premier changement. Si le dossier n'est pas
  inscriptible, la fenêtre Paramètres le dit et le réglage ne vaut que pour la session.
- `journaux\camembert-AAAAMMJJ.log` : journal local (analyses, moteur utilisé, éléments envoyés à la Corbeille,
  erreurs), dix jours conservés, jamais envoyé. Si le dossier n'est pas inscriptible : `%LOCALAPPDATA%\Camembert\journaux`.

## Paramètres

Bouton « Paramètres » (accueil et barre supérieure) :

- **Thème** : selon Windows (par défaut, suit les changements de Windows en direct), clair ou sombre.
- **Animations du diagramme** : désactivables. Elles restent aussi coupées si l'option « Effets d'animation » de
  Windows est désactivée.

## Deux moteurs d'analyse

| | Analyse rapide NTFS | Analyse standard |
|---|---|---|
| Principe | Lit la table de fichiers (MFT) du volume d'un seul trait | Parcourt les dossiers un à un, en parallèle |
| Pour | Un volume NTFS entier | Tout dossier, tout système de fichiers |
| Droits | Administrateur | Aucun |
| Voit | Tout le volume, quels que soient les droits d'accès | Seulement ce que l'utilisateur peut lire |
| Liens physiques | Listés sous chaque nom, taille comptée une seule fois | Comptés une fois par nom |
| Fichiers système NTFS (`$MFT`…) | Visibles à la racine | Invisibles |

Sur l'accueil, un clic sur un volume NTFS lance l'analyse rapide ; le lien « Analyse standard, sans droits
d'administrateur » sous chaque volume lance l'autre. « Choisir un dossier… » utilise toujours l'analyse standard.
Le moteur réellement utilisé est écrit dans la barre d'état.

**Élévation.** L'interface ne tourne jamais en administrateur. Pour l'analyse rapide, elle relance son propre
exécutable avec élévation (`--analyse-elevee`, invite UAC) ; ce second processus lit la MFT en lecture seule et
renvoie l'arbre par un canal nommé. Il n'accepte aucune commande : fermer le canal l'arrête. Si l'élévation est
refusée ou si la lecture échoue, l'analyse standard prend le relais et la barre d'état le dit.

## Utilisation

- **Deux vues**, au choix au-dessus du dessin (mémorisé) :
  - **Cercles** : le disque central est le dossier courant, chaque anneau un niveau (6 au plus).
    Clic au centre : remonter. Le changement de dossier est animé (250 ms), sauf si les animations sont coupées.
  - **Rectangles** : chaque fichier est un rectangle d'aire proportionnelle à sa taille, rangé dans le rectangle de
    son dossier ; les gros fichiers isolés sautent aux yeux. Couleurs par dossier (celles de la liste) ou, case
    « Couleurs par type de fichier », par type (celles de l'onglet Types). Les éléments trop petits pour être
    dessinés sont regroupés en un rectangle gris ; ils restent dans la liste et la recherche.

  Dans les deux vues : survol = nom, taille, pourcentage, chemin ; clic = sélection, reportée dans la liste ;
  double-clic ou molette vers l'avant = entrer dans le dossier sous le pointeur ; molette vers l'arrière ou
  Retour arrière = remonter ; clic droit = menu.
- **Liste** (onglet « Contenu du dossier ») : nom, taille, pourcentage, type, date de modification. Clic sur un
  en-tête : tri, second clic : ordre inverse. Sélection multiple avec Ctrl et Maj. Double-clic ou Entrée : entrer
  dans un dossier. Clic droit : mêmes actions ; Ctrl+C copie les chemins sélectionnés.
- **Recherche** (onglet « Recherche » ou champ en haut à droite) : porte sur toute l'analyse, sans relire le disque.
  Critères cumulables : partie du nom (sans distinction de casse), extensions (`mp4, mkv`), catégorie, fichiers ou
  dossiers, taille minimale et maximale (`500 Mo`, `1,5 Go` ; un nombre seul est en Mo).
  Double-clic sur un résultat : l'afficher dans son dossier, dans le diagramme et la liste.
- **Types** (onglet) : espace occupé par extension sur toute l'analyse (taille, pourcentage, nombre de fichiers,
  catégorie). Clic sur un type : ses fichiers sont mis en évidence dans la vue Rectangles (le reste est estompé).
  Double-clic ou Entrée : lister tous ses fichiers dans l'onglet Recherche.
- **Fil d'Ariane** : clic sur un niveau pour y revenir.
- **Élément choisi** : le dernier élément sélectionné, dans le dessin ou dans une liste, est rappelé sous le
  dessin avec ses actions : « Aperçu », « Supprimer… », « Ajouter à la liste à supprimer », « Ouvrir dans l'Explorateur ».
- **Aperçu** (bouton, clic droit ou touche Espace dans une liste) : image (JPEG, PNG, GIF, BMP, WebP, TIFF), texte
  (64 Ko au plus, encodage détecté), PDF page par page, vidéo (durée, dimensions, miniature) ; pour le reste, nom,
  taille, dates et chemin. Un fichier cloud non téléchargé n'est jamais lu, pour ne pas déclencher son téléchargement.

## Suppression

- **Supprimer…** (bouton sous le dessin, clic droit, ou touche **Suppr**) : l'élément choisi, ou toute la sélection
  de la liste. Une confirmation affiche les chemins et la taille (« Non » par défaut), puis les éléments partent dans
  la **Corbeille Windows** (API Shell `IFileOperation`), d'où ils restent restaurables.
- **Liste à supprimer** (en bas de la fenêtre) : pour rassembler des éléments de dossiers différents et les supprimer
  en une fois (« Tout supprimer… »). Y ajouter un élément ne supprime rien ; un dossier remplace ce qu'il contient
  déjà dans la liste, donc le total ne compte rien deux fois. « Vider la liste » la vide sans rien supprimer.
- La Corbeille étant sur le même disque, l'espace n'est réellement libéré qu'en la vidant (Nettoyage recommandé,
  ou Windows).
- Les tailles sont mises à jour partout sans nouvelle analyse ; ce qui n'a pas pu être supprimé est signalé avec la
  raison (et reste dans la liste à supprimer, si la suppression venait de là).

Garde-fous :

- Jamais de suppression définitive par l'application. Sur un disque sans Corbeille (clé USB, réseau), la suppression
  est refusée. Si un élément est trop gros pour la Corbeille, c'est Windows qui avertit et demande.
- Emplacements protégés, refusés avant toute suppression : tout élément à la racine d'un disque ; `Windows`,
  `Program Files`, `ProgramData` en entier ; `Users`, chaque profil et ses éléments directs (`Documents`, `Bureau`…) ;
  `AppData\Local`, `Roaming` et `LocalLow` eux-mêmes ; `System Volume Information`, `$Recycle.Bin`, `Recovery` et
  les fichiers système NTFS.
- Juste avant la suppression, chaque élément est revérifié : toujours présent, toujours un fichier (ou un dossier),
  pas devenu un lien, même date de modification et même taille qu'à l'analyse. Sinon il est laissé en place avec le
  message « Modifié depuis l'analyse » : relancer l'analyse.
- Aucun contournement des droits : un fichier verrouillé ou protégé est signalé, pas forcé.

## Nettoyage recommandé

Sur l'accueil, sous les volumes, mesuré en arrière-plan à chaque retour à l'accueil (repris de la version Python) :

| Recommandation | Action |
|---|---|
| Fichiers temporaires (`%TEMP%`, éléments de plus de 24 h) | Mettre à la Corbeille |
| Caches des navigateurs (Chrome, Edge, Brave, Firefox) | Mettre à la Corbeille |
| Rapports de plantage (`%LOCALAPPDATA%\CrashDumps`) | Mettre à la Corbeille |
| Caches développeur (npm, pip) | Mettre à la Corbeille |
| Corbeille (tous les disques) | Vider la Corbeille : **définitif**, seule action irréversible de l'application |
| Téléchargements (plus de 100 Mo) | Analyser le dossier dans le diagramme |
| Fichiers temporaires de Windows (`Windows\Temp`, Windows Update, `Minidump`, `MEMORY.DMP`) | Information ; bouton vers Paramètres › Stockage |
| `hiberfil.sys`, `Windows.old` (plus de 100 Mo) | Information : comment les supprimer avec Windows |

Chaque action demande confirmation (« Non » par défaut) et refait la liste juste avant, pour confirmer exactement
ce qui sera envoyé. Les éléments passent par les mêmes vérifications que toute suppression ; ceux qui sont ouverts par un
programme (navigateur lancé…) sont laissés en place et comptés. Camembert ne supprime rien dans le dossier Windows :
il renvoie vers les outils de Windows, qui le font avec les droits nécessaires.

## Structure

| Projet | Rôle |
|---|---|
| `Camembert.Core` | Vue rectangles (`TreemapLayout`), types de fichiers (`FileTypeStats`), nettoyage recommandé (`Cleanup`), réglages (`SettingsStore`), messages d'erreur en français (`ErrorText`), arbre compact (`FileTree`), agrégation, modèle du sunburst, recherche (`TreeSearch`), panier (`Basket`), emplacements protégés (`DeletionRules`), contrat `IScanEngine` |
| `Camembert.Scanner` | `Standard/StandardScanEngine` et `Ntfs/NtfsScanEngine` (+ `MftIndex`, décodage des enregistrements) |
| `Camembert.Windows` | Volumes, Explorateur, Corbeille (`Shell/SafeDelete`, `Shell/RecycleBin`), aperçus (`Preview/PreviewLoader`), analyse dans un processus élevé (`Elevation/ElevatedScan`) |
| `Camembert.App` | Fenêtre WPF, ViewModels, contrôle sunburst SkiaSharp, thèmes (`Themes/`), journal |

`FileTree` : tableaux parallèles indexés par identifiant de nœud (environ 55 octets par entrée, nom compris).
Les enfants d'un dossier s'ajoutent en un seul bloc (`AddChildren`), donc un parent précède toujours ses enfants.

## Règles d'analyse

- Jonctions et liens symboliques : ni suivis ni comptés (pas de boucle, pas de double comptage).
- Fichiers cloud non téléchargés (OneDrive) : 0 octet (analyse rapide : ce qui est réellement sur le disque).
- Dossier inaccessible (analyse standard) : marqué « (inaccessible) », compté pour 0, l'analyse continue.

## Limites connues

- Taille **logique** uniquement : l'espace réellement alloué (compression, fichiers creux, clusters) n'est pas affiché.
- Analyse rapide : flux de données alternatifs et index des dossiers non comptés ; un fichier à plusieurs liens
  physiques est attribué au premier nom rencontré, les autres valent 0.
- Analyse rapide : sa durée est celle de la lecture de la MFT entière (environ 1 Ko par fichier présent ou passé),
  précédée de l'écriture par Windows de ses données en attente ; sur un disque très sollicité, cette étape seule
  peut prendre plusieurs secondes.
- L'écart affiché avec l'espace utilisé selon Windows vient de ces points (et, en analyse standard, des dossiers inaccessibles).
- Les petits éléments (moins de 0,5° d'arc) sont regroupés en un segment gris dans le diagramme ; ils restent tous dans la liste.
- Le résultat s'affiche à la fin de l'analyse (pas de résultat partiel pendant l'analyse).
- La recherche garde en mémoire une copie en minuscules des noms (environ 25 octets par entrée), créée à la première recherche.
- Suppression : un dossier dont le contenu profond a changé sans que sa propre date change n'est pas détecté
  comme modifié (seule la date du dossier lui-même est comparée) ; il part alors à la Corbeille avec son contenu actuel.
- Suppression : un chemin de 260 caractères ou plus est refusé par la Corbeille Windows.
- Suppression d'un fichier à plusieurs liens physiques : l'espace n'est réellement libéré que lorsque tous ses noms
  ont disparu ; la taille « libérée » affichée est celle du nom qui la portait dans l'analyse.
- Après une analyse rapide, les fichiers que seul un administrateur peut lire sont visibles mais leur aperçu et leur
  suppression échouent (l'interface n'est pas élevée) : c'est signalé, pas contourné.
- Windows 10 version 2004 ou plus récent (exigence de SkiaSharp.Views.WPF 4).
- Les messages d'erreur venant de .NET sont remplacés par un texte français générique (« accès refusé »,
  « introuvable »…) ; ceux venant de Windows restent dans la langue du système.
- `glfw3.dll` (dépendance de SkiaSharp.Views.WPF par OpenTK) est livré mais inutilisé.

## Tests d'acceptation (section 9 du cahier des charges)

| Test | État | Comment |
|---|---|---|
| Lancement sans installation de .NET | Partiel | ZIP autonome : aucun fichier du .NET installé chargé, pas de `runtimeconfig.json`. Non essayé sur une machine sans .NET. |
| Analyse NTFS et comparaison avec un outil de référence | Partiel | Comparée à l'espace utilisé selon Windows (écart 7,6 Go sur 896 Go) et au moteur standard ; pas à WizTree. |
| Analyse d'un support exFAT ou FAT32 | Oui | Disques virtuels exFAT et FAT32 : contenu exact (noms accentués, chemin profond), écart avec Windows 22 à 60 Ko, envoi à la Corbeille du volume. |
| Annulation d'un scan sans blocage | Oui | 89 à 328 ms selon le moteur. |
| Dossier inaccessible sans plantage | Oui | Test automatique et 468 dossiers sur C:. |
| Jonctions, liens symboliques, hard links | Oui | Tests automatiques (moteur standard et MFT). |
| Navigation sunburst et synchronisation avec la liste | Oui | Vérifiée dans l'application. |
| Recherche et filtrage sur un grand volume | Oui | 4 millions d'entrées : 3 à 300 ms. |
| Sélection multiple sans double comptage | Oui | Liste à supprimer : tests automatiques et application. |
| Envoi vers la Corbeille après confirmation | Oui | Test automatique et application (fichiers d'essai). |
| Refus des dossiers système critiques | Oui | Tests automatiques et application (`C:\Windows\Web`). |
| Prévisualisation image, texte, PDF | Oui | Tests automatiques et application. |
| Thèmes clair et sombre | Oui | Vérifiés sur la version décompressée du ZIP. |
| Exécution depuis un dossier décompressé | Oui | Démarrage en 0,8 à 1 s, analyse rapide comprise (relance élevée de l'exécutable). |

